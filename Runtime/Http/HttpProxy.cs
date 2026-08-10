using System;
using System.Net;

namespace CurlUnity.Http
{
    /// <summary>
    /// HTTP / HTTPS / SOCKS 代理配置。通过 <see cref="IHttpClient.SetProxy"/> 激活。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>类型识别：</b>libcurl 依 <see cref="Url"/> 的 scheme 自动识别代理类型，
    /// 支持 <c>http://</c>、<c>https://</c>、<c>socks4://</c>、<c>socks5://</c>、
    /// <c>socks5h://</c>（后者让 DNS 在 proxy 侧解析）等。
    /// </para>
    /// <para>
    /// <b>HTTP/3 限制：</b>QUIC 基于 UDP，无法通过 HTTP CONNECT 隧道转发。
    /// 启用代理后，即使 client 的 <c>PreferredVersion</c> 为
    /// <c>PreferH3</c>，libcurl 也会回退到 HTTP/2 over TCP 经由代理建立连接。
    /// </para>
    /// </remarks>
    public sealed class HttpProxy
    {
        /// <summary>代理 URL。形如 <c>http://proxy:8080</c>、<c>socks5://host:1080</c>。</summary>
        public string Url { get; }

        /// <summary>代理的 Basic 认证凭据。<c>null</c> 表示无认证。</summary>
        public NetworkCredential Credentials { get; }

        public HttpProxy(string url, NetworkCredential credentials = null)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("proxy url cannot be null or empty", nameof(url));
            Url = url;
            Credentials = credentials;
        }
    }
}
