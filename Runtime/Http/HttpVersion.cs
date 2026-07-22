namespace CurlUnity.Http
{
    /// <summary>
    /// HTTP 协议版本。数值与 curl 的 <c>CURL_HTTP_VERSION_*</c> 定义一致。
    /// 既用于请求偏好配置(<see cref="CurlHttpClient.PreferredVersion"/>),
    /// 也用于响应中报告实际使用的版本(<see cref="IHttpResponse.Version"/>)。
    /// </summary>
    /// <remarks>
    /// 语义略有歧义:作为请求偏好时表示"希望使用此版本"(如 <see cref="PreferH3"/>
    /// 允许自动降级);作为响应结果时表示"实际协议"(不含偏好语义)。后续版本计划
    /// 拆分为 <c>HttpVersionPolicy</c> 和 <c>HttpVersion</c> 两个独立类型。
    /// </remarks>
    public enum HttpVersion
    {
        /// <summary>默认:让 libcurl 自己决定(通常协商出可用的最高版本)。</summary>
        Default = 0,
        /// <summary>HTTP/1.0。</summary>
        Http10 = 1,
        /// <summary>HTTP/1.1。</summary>
        Http11 = 2,
        /// <summary>HTTP/2。</summary>
        Http2 = 3,
        /// <summary>
        /// HTTP/3 over QUIC。作为请求偏好时若 server 不支持会降级到 HTTP/2/1.1;
        /// 响应结果里表示实际协商的就是 H3。
        /// </summary>
        Http3 = 30,
        /// <summary>
        /// "偏好 H3":请求时等价于 <see cref="Http3"/>,但语义上强调"允许降级"(与
        /// <see cref="Http3Only"/> 对比)。与 <see cref="Http3"/> 数值相同,便于复用。
        /// </summary>
        PreferH3 = Http3,
        /// <summary>
        /// 强制 HTTP/3:不允许降级,server 不支持 H3 即请求失败。
        /// 调试或特定场景使用,生产一般用 <see cref="PreferH3"/>。
        /// </summary>
        Http3Only = 31,
    }
}
