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
    /// 典型用法:
    /// <code>
    /// var form = new MultipartFormData();
    /// form.AddText("userId", "42");
    /// form.AddFile("avatar", "photo.jpg", fileBytes, "image/jpeg");
    /// await client.PostMultipartAsync(url, form);
    /// </code>
    /// </para>
    /// <para>
    /// 所有 part 数据一次性保存在内存,Build() 产出完整 byte[]。适合小~中等文件
    /// (几十 MB 以内);更大文件需走流式上传(规划中)。
    /// </para>
    /// <para>
    /// <see cref="ContentType"/> 在实例构造时即可读,包含随机生成的 boundary。
    /// </para>
    /// </remarks>
    public sealed class MultipartFormData
    {
        private readonly List<Part> _parts = new List<Part>();
        private readonly string _boundary;

        public MultipartFormData()
        {
            _boundary = "----CurlUnityBoundary" + Guid.NewGuid().ToString("N");
            ContentType = "multipart/form-data; boundary=" + _boundary;
        }

        /// <summary>Content-Type header 值,含 boundary。构造时即可读,不依赖 <see cref="Build"/>。</summary>
        public string ContentType { get; }

        /// <summary>添加文本字段。value 用 UTF-8 编码。</summary>
        public void AddText(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            _parts.Add(new Part
            {
                Name = name,
                Body = Encoding.UTF8.GetBytes(value ?? string.Empty),
            });
        }

        /// <summary>添加文件字段。</summary>
        /// <param name="name">表单字段名(后端读取 key)。</param>
        /// <param name="fileName">文件名(filename 属性)。</param>
        /// <param name="content">文件内容。</param>
        /// <param name="contentType">文件 MIME 类型,默认 application/octet-stream。</param>
        public void AddFile(string name, string fileName, byte[] content,
            string contentType = "application/octet-stream")
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("fileName required", nameof(fileName));
            if (content == null) throw new ArgumentNullException(nameof(content));
            var ct = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
            // contentType 直接写进 header,必须拒绝含 CR/LF 的值避免 header 注入
            if (ct.IndexOf('\r') >= 0 || ct.IndexOf('\n') >= 0)
                throw new ArgumentException("contentType must not contain CR or LF", nameof(contentType));
            _parts.Add(new Part
            {
                Name = name,
                FileName = fileName,
                ContentType = ct,
                Body = content,
            });
        }

        /// <summary>构造完整 multipart body。可多次调用,结果一致。</summary>
        public byte[] Build()
        {
            using var ms = new MemoryStream();
            var dashBoundary = Encoding.ASCII.GetBytes("--" + _boundary + "\r\n");
            var closeBoundary = Encoding.ASCII.GetBytes("--" + _boundary + "--\r\n");
            var crlf = new byte[] { 0x0D, 0x0A };

            foreach (var part in _parts)
            {
                ms.Write(dashBoundary, 0, dashBoundary.Length);
                var header = BuildPartHeader(part);
                ms.Write(header, 0, header.Length);
                ms.Write(part.Body, 0, part.Body.Length);
                ms.Write(crlf, 0, crlf.Length);
            }
            ms.Write(closeBoundary, 0, closeBoundary.Length);
            return ms.ToArray();
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
            public byte[] Body;
        }
    }
}
