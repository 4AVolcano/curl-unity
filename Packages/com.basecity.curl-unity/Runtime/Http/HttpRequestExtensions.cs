using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CurlUnity.Http
{
    /// <summary>
    /// <see cref="IHttpRequest"/> 的请求级便利扩展,当前主要是常见认证方式的 header
    /// 辅助。链式返回 <see cref="IHttpRequest"/> 支持 fluent 写法。
    /// </summary>
    /// <remarks>
    /// 这些扩展只是拼接 <c>Authorization</c> header 的 shortcut,不走 libcurl 的
    /// <c>CURLOPT_USERPWD</c>/<c>CURLOPT_HTTPAUTH</c>,因此不支持 Digest/NTLM。
    /// 如果调用方之前已经在 <see cref="IHttpRequest.Headers"/> 里设过
    /// <c>Authorization</c>, 这里<b>追加</b>一条新 header 而不替换, 由调用方自行保证
    /// 不重复(HTTP 规范下 Authorization 出现多次行为未定义)。
    /// </remarks>
    public static class HttpRequestExtensions
    {
        /// <summary>
        /// 添加 <c>Authorization: Bearer &lt;token&gt;</c> header。
        /// </summary>
        public static IHttpRequest WithBearerToken(this IHttpRequest request, string token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("token required", nameof(token));
            return AddAuthHeader(request, "Bearer " + token);
        }

        /// <summary>
        /// 添加 <c>Authorization: Basic &lt;base64(user:password)&gt;</c> header。user/password
        /// 按 RFC 7617 以 UTF-8 编码后 base64。
        /// </summary>
        public static IHttpRequest WithBasicAuth(this IHttpRequest request, string user, string password)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (user.IndexOf(':') >= 0)
                throw new ArgumentException("username must not contain ':' (RFC 7617)", nameof(user));
            var encoded = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(user + ":" + (password ?? string.Empty)));
            return AddAuthHeader(request, "Basic " + encoded);
        }

        private static IHttpRequest AddAuthHeader(IHttpRequest request, string value)
        {
            // Headers 是 IEnumerable<KV>, 可能是 null / array / List; 物化成 List 再 append
            var list = request.Headers as List<KeyValuePair<string, string>>
                       ?? request.Headers?.ToList()
                       ?? new List<KeyValuePair<string, string>>();
            list.Add(new KeyValuePair<string, string>("Authorization", value));
            request.Headers = list;
            return request;
        }
    }
}
