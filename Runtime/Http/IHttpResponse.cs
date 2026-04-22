using System;
using System.Collections.Generic;

namespace CurlUnity.Http
{
    /// <summary>
    /// HTTP 响应。由 <see cref="IHttpClient.SendAsync"/> 返回。
    /// </summary>
    /// <remarks>
    /// 持有底层 libcurl easy handle;**必须 <c>Dispose()</c>**(或 <c>using</c>),否则
    /// handle 会泄漏。在 Dispose 之前 <see cref="ContentType"/> / <see cref="Headers"/> 等
    /// 属性都能反复读取。
    /// </remarks>
    public interface IHttpResponse : IDisposable
    {
        /// <summary>
        /// 底层 easy handle 是否已释放。为 true 后,依赖 <c>curl_easy_getinfo</c>
        /// 的懒加载属性(<see cref="Version"/> / <see cref="ContentType"/> /
        /// <see cref="ContentLength"/> / <see cref="EffectiveUrl"/> / <see cref="RedirectCount"/>
        /// / <see cref="Headers"/>) 会返回默认值或 null;已缓存到托管字段的
        /// <see cref="HasResponse"/> / <see cref="StatusCode"/> / <see cref="ErrorCode"/> /
        /// <see cref="ErrorMessage"/> / <see cref="Body"/> 仍返回原值。
        /// </summary>
        bool IsDisposed { get; }

        /// <summary>
        /// 是否收到了完整的 HTTP 响应(即 libcurl 未报网络/协议错误)。
        /// false 时多半是 DNS/TCP/TLS/超时等连接阶段失败,可看 <see cref="ErrorCode"/>
        /// 和 <see cref="ErrorMessage"/>。HTTP 4xx/5xx 状态码**仍算有响应**(true)。
        /// </summary>
        bool HasResponse { get; }

        /// <summary>HTTP 状态码。<see cref="HasResponse"/>=false 时为 0。</summary>
        int StatusCode { get; }

        /// <summary>实际使用的 HTTP 协议版本。可能与请求偏好不同(例如启用代理会从 H3 降到 H2)。</summary>
        HttpVersion Version { get; }

        /// <summary>libcurl CURLcode。0 = 成功。</summary>
        int ErrorCode { get; }

        /// <summary>错误描述,对应 <c>curl_easy_strerror(ErrorCode)</c>。成功时为 null。</summary>
        string ErrorMessage { get; }

        /// <summary>
        /// 响应体。流式模式(设置 <see cref="IHttpRequest.OnDataReceived"/>)下为 null,
        /// 数据已通过回调逐块交付。若请求开启了 <see cref="IHttpRequest.AutoDecompressResponse"/>,
        /// 这里是解压后的明文。
        /// </summary>
        byte[] Body { get; }

        // --- curl 内部缓存的常用 header 信息（始终可用，无需开启 EnableResponseHeaders）---

        /// <summary>Content-Type（如 "application/json; charset=utf-8"），无则 null。</summary>
        string ContentType { get; }

        /// <summary>Content-Length，未知时为 -1。</summary>
        long ContentLength { get; }

        /// <summary>重定向后的最终 URL。无重定向时等于请求 URL。</summary>
        string EffectiveUrl { get; }

        /// <summary>重定向次数。</summary>
        int RedirectCount { get; }

        // --- 完整响应头（需要 EnableResponseHeaders=true，否则为 null）---

        /// <summary>
        /// 所有响应头。key 为小写 header name,value 为该 header 的所有值。
        /// 仅在请求设置 <see cref="IHttpRequest.EnableResponseHeaders"/>=true 时可用,否则返回 null。
        /// </summary>
        IReadOnlyDictionary<string, string[]> Headers { get; }
    }
}
