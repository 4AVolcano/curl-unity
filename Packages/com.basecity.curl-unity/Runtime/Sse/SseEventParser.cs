using System;
using System.IO;
using System.Text;

namespace CurlUnity.Sse
{
    /// <summary>
    /// Server-Sent Events (<c>text/event-stream</c>) 增量解析器。喂入一块块原始字节
    /// (来自流式下载回调)，按 WHATWG HTML "parse an event stream" 算法产出
    /// <see cref="SseEvent"/>。纯协议、零网络依赖、可独立复用。
    /// </summary>
    /// <remarks>
    /// 设计为两层：
    /// <list type="bullet">
    ///   <item><b>字节层</b>：按字节扫描行尾 (<c>\n</c>/<c>\r</c>/<c>\r\n</c>)，凑齐整行后再
    ///   UTF-8 解码。行尾符都是 ASCII，UTF-8 多字节续字节恒 ≥0x80，故按字节切行不会劈开任何
    ///   多字节字符——chunk 在字符中间切断时半个字符留在缓冲，下次 <see cref="Feed"/> 续上。</item>
    ///   <item><b>行层</b>：<c>field:value</c> 累积，遇空行 dispatch。</item>
    /// </list>
    /// 非线程安全：约定由单一线程 (流式回调线程) 顺序调用。
    /// <para>
    /// <b>行长无上限</b>：按规范，单行可以任意长。本解析器会把未遇到行终止符的字节一直缓冲，
    /// 内存随之增长；逼近运行时数组上限 (<see cref="MaxLineCapacity"/>) 时抛
    /// <see cref="InvalidDataException"/>。需要更早的防护请在传输层/上层限制输入。
    /// </para>
    /// </remarks>
    public sealed class SseEventParser
    {
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        // netstandard2.1 无 Array.MaxLength，用 byte[] 的运行时上限常量（与 .NET 6+ Array.MaxLength 同值）
        private const int MaxLineCapacity = 0x7FFFFFC7;

        // —— 字节层 ——
        private byte[] _lineBuf = new byte[256]; // 当前未完成行的原始字节
        private int _lineLen;
        private bool _sawCr;      // 上一字节是 \r，用于跨 chunk 合并 \r\n
        private bool _bomChecked; // 流首 BOM 是否已检查
        private int _bomMatch;    // 已匹配的 BOM 前缀字节数 (0..3)，容忍跨 chunk

        // —— 行层 ——
        private readonly StringBuilder _dataBuffer = new();
        private string _eventTypeBuffer = "";
        private bool _dataPresent;          // 本事件是否出现过 data 字段（决定空行是否 dispatch）
        private string _lastEventIdBuffer = ""; // last event ID buffer：读到 id 即更新，dispatch 时才同步到 LastEventId

        /// <summary>
        /// 已确认的 last event ID（WHATWG 的 "last event ID string"）。仅在 dispatch 时从内部
        /// buffer 同步，半事件里的 <c>id</c> 在 <see cref="Reset"/> 后会被丢弃。跨事件 / 跨
        /// <see cref="Reset"/> 保留，重连时可作 <c>Last-Event-ID</c> 注入。从未确认过时为空字符串。
        /// </summary>
        public string LastEventId { get; private set; } = "";

        /// <summary>
        /// 服务端 <c>retry:</c> 字段最近值（毫秒）；从未出现时为 <c>null</c>。
        /// 纯数字但超过 <see cref="int.MaxValue"/> 时 clamp 到 <see cref="int.MaxValue"/>。
        /// </summary>
        public int? RetryMilliseconds { get; private set; }

        /// <summary>
        /// 喂入一块原始字节，按需触发 <paramref name="onEvent"/>（每解析出一个完整事件回调一次）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> 或 <paramref name="onEvent"/> 为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/>/<paramref name="count"/> 超出 <paramref name="buffer"/> 范围。</exception>
        /// <exception cref="InvalidDataException">单行长度超过运行时上限（通常意味着对端一直不发行终止符）。</exception>
        public void Feed(byte[] buffer, int offset, int count, Action<SseEvent> onEvent)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (onEvent == null) throw new ArgumentNullException(nameof(onEvent));
            // 用减法形式比较，避免 offset + count 整数溢出
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count),
                    "offset/count 超出 buffer 范围");

            int end = offset + count;
            int i = offset;

            // 1) 流首 BOM 剥离（仅一次，容忍跨 chunk）
            while (!_bomChecked && i < end)
            {
                if (buffer[i] == Bom[_bomMatch])
                {
                    _bomMatch++;
                    i++;
                    if (_bomMatch == Bom.Length) _bomChecked = true;
                }
                else
                {
                    // 不是 BOM：把之前误吞的前缀字节当普通数据吐回行缓冲
                    for (int k = 0; k < _bomMatch; k++) AppendByte(Bom[k]);
                    _bomChecked = true;
                    break;
                }
            }

            // 2) 逐字节按行尾切分
            for (; i < end; i++)
            {
                byte b = buffer[i];

                if (_sawCr)
                {
                    _sawCr = false;
                    if (b == (byte)'\n') continue; // \r\n 的 \n：行已在 \r 处结束，吞掉
                }

                if (b == (byte)'\r')
                {
                    EndLine(onEvent);
                    _sawCr = true;
                }
                else if (b == (byte)'\n')
                {
                    EndLine(onEvent);
                }
                else
                {
                    AppendByte(b);
                }
            }
        }

        /// <summary>
        /// 丢弃半行 / 半事件状态（重连前调用），但保留已确认的 <see cref="LastEventId"/> 与
        /// <see cref="RetryMilliseconds"/>。半事件里未确认的 <c>id</c> 会被回退丢弃。
        /// 新连接是新流，故重新检查 BOM。
        /// </summary>
        public void Reset()
        {
            _lineLen = 0;
            _sawCr = false;
            _bomChecked = false;
            _bomMatch = 0;
            _dataBuffer.Clear();
            _eventTypeBuffer = "";
            _dataPresent = false;
            _lastEventIdBuffer = LastEventId; // 丢弃半事件里未确认的 id，回退到已确认值
        }

        private void AppendByte(byte b)
        {
            if (_lineLen == _lineBuf.Length)
                GrowLineBuffer();
            _lineBuf[_lineLen++] = b;
        }

        private void GrowLineBuffer()
        {
            if (_lineBuf.Length >= MaxLineCapacity)
                throw new InvalidDataException(
                    $"SSE 单行超过 {MaxLineCapacity} 字节上限（对端缺少行终止符？）");
            // long 乘法防止 int 溢出；逼近上限时 clamp 到 MaxLineCapacity
            int newCap = (int)Math.Min((long)_lineBuf.Length * 2, MaxLineCapacity);
            Array.Resize(ref _lineBuf, newCap);
        }

        private void EndLine(Action<SseEvent> onEvent)
        {
            string line = Encoding.UTF8.GetString(_lineBuf, 0, _lineLen);
            _lineLen = 0;
            ProcessLine(line, onEvent);
        }

        private void ProcessLine(string line, Action<SseEvent> onEvent)
        {
            if (line.Length == 0)
            {
                Dispatch(onEvent);
                return;
            }
            if (line[0] == ':') return; // 注释行，忽略

            int colon = line.IndexOf(':');
            string field, value;
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line.Substring(0, colon);
                value = line.Substring(colon + 1);
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1); // 去一个前导空格
            }

            switch (field)
            {
                case "data":
                    _dataBuffer.Append(value).Append('\n');
                    _dataPresent = true;
                    break;
                case "event":
                    _eventTypeBuffer = value;
                    break;
                case "id":
                    // 含 NUL 则忽略本次设置；否则写入 buffer（dispatch 时才确认到 LastEventId）
                    if (value.IndexOf('\0') < 0) _lastEventIdBuffer = value;
                    break;
                case "retry":
                    // 纯 ASCII 数字才解析；超过 int.MaxValue 时 clamp（规范要求 digit-only 即视为整数）
                    if (IsAllAsciiDigits(value))
                        RetryMilliseconds = int.TryParse(value, out int ms) ? ms : int.MaxValue;
                    break;
                // 其它字段（含未知）忽略
            }
        }

        private void Dispatch(Action<SseEvent> onEvent)
        {
            // 规范 dispatch step 1：先把 last event ID buffer 同步到已确认的 LastEventId
            // （即使本块没有 data、不触发事件，也要同步）
            LastEventId = _lastEventIdBuffer;

            if (!_dataPresent)
            {
                _eventTypeBuffer = ""; // 无 data 不 dispatch，但清掉残留的 event 类型
                return;
            }

            string data = _dataBuffer.ToString();
            if (data.Length > 0 && data[data.Length - 1] == '\n')
                data = data.Substring(0, data.Length - 1); // 去末尾一个 \n

            string type = _eventTypeBuffer.Length == 0 ? "message" : _eventTypeBuffer;
            onEvent(new SseEvent(type, data, LastEventId));

            _dataBuffer.Clear();
            _eventTypeBuffer = "";
            _dataPresent = false;
            // LastEventId / RetryMilliseconds 跨事件保留
        }

        private static bool IsAllAsciiDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
