using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CurlUnity.Http
{
    /// <summary>
    /// multipart/form-data 请求体构造器,按 RFC 7578 规范拼装 body,随机生成 boundary。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 典型用法(小~中等文件):
    /// <code>
    /// var form = new MultipartFormData();
    /// form.AddText("userId", "42");
    /// form.AddFile("avatar", "photo.jpg", fileBytes, "image/jpeg");
    /// await client.PostMultipartAsync(url, form);
    /// </code>
    /// </para>
    /// <para>
    /// 大文件场景用 <see cref="AddFile(string, string, System.IO.Stream, long, string)"/>
    /// 提交 <see cref="System.IO.Stream"/>,配合 <see cref="BuildStream"/> 和 <see cref="ContentLength"/>
    /// 走流式上传,避免全量读入内存。<see cref="HasStreamParts"/> 为 <c>true</c> 时
    /// <see cref="HttpClientExtensions.PostMultipartAsync"/> 会自动路由到 <see cref="IHttpRequest.BodyStream"/> 通路。
    /// </para>
    /// <para>
    /// <see cref="ContentType"/> 在实例构造时即可读,包含随机生成的 boundary。
    /// </para>
    /// <para>
    /// <b>冻结语义</b>:首次调用 <see cref="Build"/> 或 <see cref="BuildStream"/> 后,
    /// form 进入 frozen 状态,再调 <see cref="AddText"/>/<see cref="AddFile(string, string, byte[], string)"/>/
    /// <see cref="AddFile(string, string, System.IO.Stream, long, string)"/> 抛 <see cref="InvalidOperationException"/>。
    /// Build/BuildStream 本身可重复调用(产出一致)。
    /// </para>
    /// </remarks>
    public sealed class MultipartFormData
    {
        private static readonly byte[] CRLF = new byte[] { 0x0D, 0x0A };

        private readonly List<Part> _parts = new List<Part>();
        private readonly string _boundary;
        private readonly byte[] _dashBoundary;     // "--<boundary>\r\n"
        private readonly byte[] _closeBoundary;    // "--<boundary>--\r\n"

        // HasStreamParts / ContentLength 做增量维护,避免每次访问都遍历 _parts。
        // 所有 mutation 只发生在 AddText / AddFile,这两处同步更新两个字段。
        private bool _hasStreamParts;
        private long _contentLength;

        // 首次 Build/BuildStream 后置 true,后续 Add* 拒绝;避免 form 在序列化进行
        // 中被改动导致 ContentLength 与实际产出不一致。
        private bool _frozen;

        public MultipartFormData()
        {
            _boundary = "----CurlUnityBoundary" + Guid.NewGuid().ToString("N");
            _dashBoundary = Encoding.ASCII.GetBytes("--" + _boundary + "\r\n");
            _closeBoundary = Encoding.ASCII.GetBytes("--" + _boundary + "--\r\n");
            ContentType = "multipart/form-data; boundary=" + _boundary;
            _contentLength = _closeBoundary.Length;  // 空 form 的 body 只有 closing boundary
        }

        /// <summary>Content-Type header 值,含 boundary。构造时即可读,不依赖 <see cref="Build"/>/<see cref="BuildStream"/>。</summary>
        public string ContentType { get; }

        /// <summary>当前 form 里是否存在 Stream part;有则必须走 <see cref="BuildStream"/>。</summary>
        public bool HasStreamParts => _hasStreamParts;

        /// <summary>
        /// 完整 body 的字节长度(boundary + headers + bodies + closing)。不会读取任何 Stream,
        /// 只累加预算值,可在不消耗数据的前提下作为 <c>Content-Length</c> 提交。
        /// </summary>
        public long ContentLength => _contentLength;

        /// <summary>
        /// Part 添加后,增量更新 ContentLength 与 HasStreamParts。调用前确保
        /// <c>part.HeaderBytes</c> 已填充、Payload 长度已确定。
        /// </summary>
        private void AppendPart(Part part)
        {
            _parts.Add(part);
            _contentLength += _dashBoundary.Length + part.HeaderBytes.Length + part.PayloadLength + CRLF.Length;
            if (part.StreamBody != null) _hasStreamParts = true;
        }

        private void EnsureMutable()
        {
            if (_frozen)
                throw new InvalidOperationException(
                    "MultipartFormData 已经 Build/BuildStream, 不允许再修改; 请新建实例");
        }

        /// <summary>添加文本字段。value 用 UTF-8 编码。</summary>
        public void AddText(string name, string value)
        {
            EnsureMutable();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            var part = new Part
            {
                Name = name,
                Body = Encoding.UTF8.GetBytes(value ?? string.Empty),
            };
            part.HeaderBytes = BuildPartHeader(part);
            AppendPart(part);
        }

        /// <summary>添加内存中的文件字段。</summary>
        public void AddFile(string name, string fileName, byte[] content,
            string contentType = "application/octet-stream")
        {
            EnsureMutable();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("fileName required", nameof(fileName));
            if (content == null) throw new ArgumentNullException(nameof(content));
            var ct = ValidateContentType(contentType);
            var part = new Part
            {
                Name = name,
                FileName = fileName,
                ContentType = ct,
                Body = content,
            };
            part.HeaderBytes = BuildPartHeader(part);
            AppendPart(part);
        }

        /// <summary>
        /// 添加流式文件字段。用于大文件避免全量读入内存;必须给 <paramref name="length"/>
        /// 以便提前算出 <c>Content-Length</c>。Stream 生命周期归调用方,本类不会 Dispose。
        /// </summary>
        /// <param name="length">Stream 从当前 Position 起将被读取的字节数。必须准确,
        /// 少于该值会导致发送失败。</param>
        public void AddFile(string name, string fileName, Stream content, long length,
            string contentType = "application/octet-stream")
        {
            EnsureMutable();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("fileName required", nameof(fileName));
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "length must be >= 0");
            if (!content.CanRead) throw new ArgumentException("stream must be readable", nameof(content));
            var ct = ValidateContentType(contentType);
            var part = new Part
            {
                Name = name,
                FileName = fileName,
                ContentType = ct,
                StreamBody = content,
                StreamLength = length,
            };
            part.HeaderBytes = BuildPartHeader(part);
            AppendPart(part);
        }

        /// <summary>构造完整 multipart body。所有 part 必须是内存数据;含 Stream part 时请改用 <see cref="BuildStream"/>。</summary>
        public byte[] Build()
        {
            if (HasStreamParts)
                throw new InvalidOperationException(
                    "form 含 Stream part,请用 BuildStream() 或直接通过 PostMultipartAsync 提交");

            // 预算总长已知, 预分配 capacity 避免 MemoryStream 内部多次扩容/复制
            long predicted = ContentLength;
            int initialCapacity = predicted <= int.MaxValue ? (int)predicted : 0;
            using var ms = new MemoryStream(initialCapacity);
            foreach (var part in _parts)
            {
                ms.Write(_dashBoundary, 0, _dashBoundary.Length);
                ms.Write(part.HeaderBytes, 0, part.HeaderBytes.Length);
                ms.Write(part.Body, 0, part.Body.Length);
                ms.Write(CRLF, 0, CRLF.Length);
            }
            ms.Write(_closeBoundary, 0, _closeBoundary.Length);
            _frozen = true;
            return ms.ToArray();
        }

        /// <summary>
        /// 返回按需产出 multipart body 的只读 Stream。Stream part 的数据按调用 <c>Read</c>
        /// 时从源 Stream 拉取,整个 body 不会一次性进内存。配合 <see cref="IHttpRequest.BodyStream"/>
        /// 使用,设 <see cref="IHttpRequest.BodyLength"/> = <see cref="ContentLength"/>。
        /// </summary>
        /// <remarks>
        /// 调用后 form 进入 frozen 状态,后续 <c>AddText</c>/<c>AddFile</c> 抛
        /// <see cref="InvalidOperationException"/>;同一 form 可重复调用 BuildStream
        /// 产出独立的 stream 对象。
        /// </remarks>
        public Stream BuildStream()
        {
            _frozen = true;
            return new MultipartStream(_parts, _dashBoundary, _closeBoundary);
        }

        private static string ValidateContentType(string contentType)
        {
            var ct = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
            // contentType 直接写进 header,必须拒绝含 CR/LF 的值避免 header 注入
            if (ct.IndexOf('\r') >= 0 || ct.IndexOf('\n') >= 0)
                throw new ArgumentException("contentType must not contain CR or LF", nameof(contentType));
            return ct;
        }

        private static byte[] BuildPartHeader(Part part)
        {
            var sb = new StringBuilder();
            if (part.FileName != null)
            {
                sb.Append("Content-Disposition: form-data; name=\"")
                  .Append(EscapeFormName(part.Name))
                  .Append("\"; filename=\"")
                  .Append(EscapeFormName(part.FileName))
                  .Append("\"\r\n");
                sb.Append("Content-Type: ").Append(part.ContentType).Append("\r\n");
            }
            else
            {
                sb.Append("Content-Disposition: form-data; name=\"")
                  .Append(EscapeFormName(part.Name))
                  .Append("\"\r\n");
                // 显式 UTF-8 charset: AddText 以 UTF-8 编码 value,有些后端默认按
                // US-ASCII 解析会乱码;显式声明避免歧义。
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            }
            sb.Append("\r\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>
        /// RFC 7578 §4.2: 字段/文件名里的双引号、CR、LF 需转义成百分号编码。
        /// 反斜杠额外转义,避免末尾 '\' 把后随的 '"' 转成 escape 序列导致
        /// quoted-string 未闭合(可被宽容 parser 利用做 header 注入)。
        /// </summary>
        private static string EscapeFormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "%5C")
                    .Replace("\"", "%22")
                    .Replace("\r", "%0D")
                    .Replace("\n", "%0A");
        }

        private sealed class Part
        {
            public string Name;
            public string FileName;      // null = 文本字段
            public string ContentType;   // 仅文件字段
            public byte[] HeaderBytes;   // 预计算的 part header (Content-Disposition 等)
            public byte[] Body;          // byte[] part,与 StreamBody 互斥
            public Stream StreamBody;    // stream part
            public long StreamLength;    // 仅 stream part 有效

            public long PayloadLength => StreamBody != null ? StreamLength : (Body?.Length ?? 0);
        }

        /// <summary>
        /// 按需串行化各 part 的只读 Stream。不持有 user Stream 所有权。
        /// 持有 parts 引用; form 在 BuildStream 后进入 frozen 状态, parts 不会再变。
        /// </summary>
        private sealed class MultipartStream : Stream
        {
            private readonly List<Part> _parts;
            private readonly byte[] _dashBoundary;
            private readonly byte[] _closeBoundary;

            private int _partIndex;    // 当前 part 下标;== _parts.Count 表示进入 closing 阶段
            private int _phase;        // 0=dashBoundary, 1=header, 2=body, 3=trailing CRLF
            private long _phaseOffset; // 当前 phase 已读字节数
            private bool _closed;

            public MultipartStream(List<Part> parts, byte[] dashBoundary, byte[] closeBoundary)
            {
                _parts = parts;
                _dashBoundary = dashBoundary;
                _closeBoundary = closeBoundary;
            }

            public override bool CanRead => !_closed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_closed) throw new ObjectDisposedException(nameof(MultipartStream));
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (buffer.Length - offset < count)
                    throw new ArgumentException(
                        "offset + count exceeds buffer length", nameof(count));
                if (count == 0) return 0;

                // state machine: 每次 Read 填当前 phase 的剩余字节,拉够一块就返回;
                // 拉空则推进到下一个 phase。调用方多次 Read 串起来就是完整 body。
                while (true)
                {
                    if (_partIndex < _parts.Count)
                    {
                        var part = _parts[_partIndex];
                        switch (_phase)
                        {
                            case 0: // dashBoundary
                            {
                                int remaining = _dashBoundary.Length - (int)_phaseOffset;
                                if (remaining > 0)
                                {
                                    int n = Math.Min(remaining, count);
                                    Buffer.BlockCopy(_dashBoundary, (int)_phaseOffset, buffer, offset, n);
                                    _phaseOffset += n;
                                    return n;
                                }
                                _phase = 1; _phaseOffset = 0;
                                continue;
                            }
                            case 1: // header
                            {
                                int remaining = part.HeaderBytes.Length - (int)_phaseOffset;
                                if (remaining > 0)
                                {
                                    int n = Math.Min(remaining, count);
                                    Buffer.BlockCopy(part.HeaderBytes, (int)_phaseOffset, buffer, offset, n);
                                    _phaseOffset += n;
                                    return n;
                                }
                                _phase = 2; _phaseOffset = 0;
                                continue;
                            }
                            case 2: // body
                            {
                                long totalLen = part.PayloadLength;
                                long remaining = totalLen - _phaseOffset;
                                if (remaining > 0)
                                {
                                    int wanted = (int)Math.Min(remaining, count);
                                    int n;
                                    if (part.StreamBody != null)
                                    {
                                        n = part.StreamBody.Read(buffer, offset, wanted);
                                        if (n <= 0)
                                        {
                                            // stream 提前 EOF 但声明 length 还未读完 → 数据不足,必须失败而非
                                            // 静默发送短 body。上层 READFUNCTION 会把本异常转成上传失败。
                                            throw new IOException(
                                                $"Multipart part '{part.Name}' stream ended after {_phaseOffset} bytes, " +
                                                $"expected {part.StreamLength}.");
                                        }
                                    }
                                    else
                                    {
                                        n = wanted;
                                        Buffer.BlockCopy(part.Body, (int)_phaseOffset, buffer, offset, n);
                                    }
                                    _phaseOffset += n;
                                    return n;
                                }
                                _phase = 3; _phaseOffset = 0;
                                continue;
                            }
                            case 3: // trailing CRLF
                            {
                                int remaining = CRLF.Length - (int)_phaseOffset;
                                if (remaining > 0)
                                {
                                    int n = Math.Min(remaining, count);
                                    Buffer.BlockCopy(CRLF, (int)_phaseOffset, buffer, offset, n);
                                    _phaseOffset += n;
                                    return n;
                                }
                                _partIndex++;
                                _phase = 0; _phaseOffset = 0;
                                continue;
                            }
                        }
                    }
                    // closing boundary
                    {
                        int remaining = _closeBoundary.Length - (int)_phaseOffset;
                        if (remaining > 0)
                        {
                            int n = Math.Min(remaining, count);
                            Buffer.BlockCopy(_closeBoundary, (int)_phaseOffset, buffer, offset, n);
                            _phaseOffset += n;
                            return n;
                        }
                        return 0; // EOF
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                _closed = true;
                base.Dispose(disposing);
            }
        }
    }
}
