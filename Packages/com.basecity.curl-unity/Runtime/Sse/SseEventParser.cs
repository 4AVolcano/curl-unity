using System;
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
    /// </remarks>
    public sealed class SseEventParser
    {
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        // —— 字节层 ——
        private byte[] _lineBuf = new byte[256]; // 当前未完成行的原始字节
        private int _lineLen;
        private bool _sawCr;      // 上一字节是 \r，用于跨 chunk 合并 \r\n
        private bool _bomChecked; // 流首 BOM 是否已检查
        private int _bomMatch;    // 已匹配的 BOM 前缀字节数 (0..3)，容忍跨 chunk

        // —— 行层 ——
        private readonly StringBuilder _dataBuffer = new();
        private string _eventTypeBuffer = "";
        private bool _dataPresent; // 本事件是否出现过 data 字段（决定空行是否 dispatch）

        /// <summary>
        /// 最近一次有效 <c>id</c> 字段值，跨事件 / 跨 <see cref="Reset"/> 保留。
        /// 从未出现过 <c>id</c> 时为空字符串。重连时可作 <c>Last-Event-ID</c> 注入。
        /// </summary>
        public string LastEventId { get; private set; } = "";

        /// <summary>服务端 <c>retry:</c> 字段最近值（毫秒）；从未出现时为 <c>null</c>。</summary>
        public int? RetryMilliseconds { get; private set; }

        /// <summary>
        /// 喂入一块原始字节，按需触发 <paramref name="onEvent"/>（每解析出一个完整事件回调一次）。
        /// </summary>
        public void Feed(byte[] buffer, int offset, int count, Action<SseEvent> onEvent)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (onEvent == null) throw new ArgumentNullException(nameof(onEvent));

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
        /// 丢弃半行 / 半事件状态（重连前调用），但保留 <see cref="LastEventId"/> 与
        /// <see cref="RetryMilliseconds"/>。新连接是新流，故重新检查 BOM。
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
        }

        private void AppendByte(byte b)
        {
            if (_lineLen == _lineBuf.Length)
                Array.Resize(ref _lineBuf, _lineBuf.Length * 2);
            _lineBuf[_lineLen++] = b;
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
                    if (value.IndexOf('\0') < 0) LastEventId = value; // 含 NUL 则忽略本次设置
                    break;
                case "retry":
                    if (IsAllAsciiDigits(value) && int.TryParse(value, out int ms))
                        RetryMilliseconds = ms;
                    break;
                // 其它字段（含未知）忽略
            }
        }

        private void Dispatch(Action<SseEvent> onEvent)
        {
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
