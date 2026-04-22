using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CurlUnity.Http
{
    /// <summary>
    /// <see cref="IHttpClient"/> 的便利扩展方法:常见方法/内容类型的 one-liner 封装。
    /// 需要更多控制时直接构造 <see cref="HttpRequest"/> 并调 <see cref="IHttpClient.SendAsync"/>。
    /// </summary>
    public static class HttpClientExtensions
    {
        /// <summary>发送一个 GET 请求。</summary>
        public static Task<IHttpResponse> GetAsync(this IHttpClient client, string url,
            CancellationToken ct = default)
        {
            var req = new HttpRequest { Method = HttpMethod.Get, Url = url };
            return client.SendAsync(req, ct);
        }

        /// <summary>发送一个 POST 请求,body 为 raw bytes,附带指定 <paramref name="contentType"/>。</summary>
        public static Task<IHttpResponse> PostAsync(this IHttpClient client, string url,
            byte[] body, string contentType = "application/octet-stream",
            CancellationToken ct = default)
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = url,
                Body = body,
                Headers = new[] { new KeyValuePair<string, string>("Content-Type", contentType) }
            };
            return client.SendAsync(req, ct);
        }

        /// <summary>
        /// 以 <c>application/json</c> 发送 POST 请求。<paramref name="json"/> 会按 UTF-8 编码。
        /// </summary>
        public static Task<IHttpResponse> PostJsonAsync(this IHttpClient client, string url,
            string json, CancellationToken ct = default)
        {
            return client.PostAsync(url, Encoding.UTF8.GetBytes(json), "application/json", ct);
        }

        /// <summary>发送一个 PUT 请求,body 为 raw bytes,附带指定 <paramref name="contentType"/>。</summary>
        public static Task<IHttpResponse> PutAsync(this IHttpClient client, string url,
            byte[] body, string contentType = "application/octet-stream",
            CancellationToken ct = default)
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Put,
                Url = url,
                Body = body,
                Headers = new[] { new KeyValuePair<string, string>("Content-Type", contentType) }
            };
            return client.SendAsync(req, ct);
        }

        /// <summary>发送一个 DELETE 请求,无 body。</summary>
        public static Task<IHttpResponse> DeleteAsync(this IHttpClient client, string url,
            CancellationToken ct = default)
        {
            var req = new HttpRequest { Method = HttpMethod.Delete, Url = url };
            return client.SendAsync(req, ct);
        }

        /// <summary>
        /// POST <c>application/x-www-form-urlencoded</c> 表单。键值按 RFC 3986 做
        /// percent-encoding 后拼 <c>k1=v1&amp;k2=v2</c> 提交。
        /// </summary>
        /// <remarks>
        /// 接受 <see cref="IEnumerable{T}"/>,支持重复 key(OAuth <c>scope=a&amp;scope=b</c>
        /// 之类场景);传 <see cref="Dictionary{TKey, TValue}"/> 也可(它就是
        /// <c>IEnumerable&lt;KeyValuePair&gt;</c> 的子类型)。
        /// </remarks>
        public static Task<IHttpResponse> PostFormAsync(this IHttpClient client, string url,
            IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct = default)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));

            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in fields)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new ArgumentException("form field key cannot be null or empty", nameof(fields));
                if (!first) sb.Append('&');
                first = false;
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
            }

            return client.PostAsync(url, Encoding.UTF8.GetBytes(sb.ToString()),
                "application/x-www-form-urlencoded", ct);
        }

        /// <summary>POST multipart/form-data 表单。含 Stream part 时自动走流式上传。</summary>
        public static Task<IHttpResponse> PostMultipartAsync(this IHttpClient client, string url,
            MultipartFormData form, CancellationToken ct = default)
        {
            if (form == null) throw new ArgumentNullException(nameof(form));
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = url,
                Headers = new[] { new KeyValuePair<string, string>("Content-Type", form.ContentType) },
            };
            if (form.HasStreamParts)
            {
                // 流式: Stream part 按需从源读取,不全量进内存。BodyLength 取自 form 预算值,
                // 发 Content-Length header;libcurl 据此按固定长度拉数据。
                req.BodyStream = form.BuildStream();
                req.BodyLength = form.ContentLength;
            }
            else
            {
                req.Body = form.Build();
            }
            return client.SendAsync(req, ct);
        }
    }
}
