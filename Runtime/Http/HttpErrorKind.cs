namespace CurlUnity.Http
{
    /// <summary>
    /// <see cref="CurlHttpException"/> 的语义分类。提供给调用方做 switch / 重试策略判断,
    /// 避免直接依赖 libcurl 的 <c>CURLcode</c> 数值。
    /// </summary>
    /// <remarks>
    /// 分类粒度按"使用者会做的响应决策"划分(重试、改配置、上报),而不是 curl 内部子系统。
    /// 不在下面已列分类里的 <c>CURLcode</c>(例如 FTP/LDAP/TFTP/SSH/RTSP 专用错误或未来新增的值)
    /// 统一落到 <see cref="Unknown"/>;此时 <see cref="CurlHttpException.CurlCode"/> 仍保留原始数值。
    /// </remarks>
    public enum HttpErrorKind
    {
        /// <summary>
        /// 未分类。0 是枚举默认值,作兜底和未初始化哨兵。
        /// 未列入其它分类的 <c>CURLcode</c> 也归到这里。
        /// </summary>
        Unknown = 0,

        /// <summary>URL 格式非法或协议不支持。对应 CURLE_URL_MALFORMAT / CURLE_UNSUPPORTED_PROTOCOL。</summary>
        InvalidUrl,

        /// <summary>DNS 解析失败(主机或代理)。对应 CURLE_COULDNT_RESOLVE_HOST / CURLE_COULDNT_RESOLVE_PROXY。</summary>
        DnsFailed,

        /// <summary>TCP 建连失败(端口不通、服务器拒绝)。对应 CURLE_COULDNT_CONNECT。</summary>
        ConnectFailed,

        /// <summary>
        /// TLS/SSL 握手或证书相关失败。对应 CURLE_SSL_* 系列、CURLE_PEER_FAILED_VERIFICATION 等。
        /// </summary>
        TlsError,

        /// <summary>请求超过 <c>TimeoutMs</c> 或 <c>ConnectTimeoutMs</c>。对应 CURLE_OPERATION_TIMEDOUT。</summary>
        Timeout,

        /// <summary>
        /// 传输中途 I/O 失败(send/recv 被中断、短读等)。对应 CURLE_SEND_ERROR / CURLE_RECV_ERROR /
        /// CURLE_PARTIAL_FILE / CURLE_GOT_NOTHING 等。常见于弱网,通常可重试。
        /// </summary>
        NetworkIo,

        /// <summary>
        /// HTTP/2/3 帧错、奇怪响应、编码错。对应 CURLE_HTTP2 / CURLE_HTTP2_STREAM / CURLE_HTTP3 /
        /// CURLE_QUIC_CONNECT_ERROR / CURLE_WEIRD_SERVER_REPLY / CURLE_BAD_CONTENT_ENCODING 等。
        /// </summary>
        ProtocolError,

        /// <summary>代理握手失败。对应 CURLE_PROXY。</summary>
        ProxyError,

        /// <summary>重定向超出 libcurl 默认上限。对应 CURLE_TOO_MANY_REDIRECTS。</summary>
        TooManyRedirects,

        /// <summary>libcurl 报告内存不足。对应 CURLE_OUT_OF_MEMORY。</summary>
        OutOfMemory,

        /// <summary>
        /// libcurl 内部 setup 失败。对应 CURLE_FAILED_INIT / CURLE_UNKNOWN_OPTION /
        /// CURLE_SETOPT_OPTION_SYNTAX / CURLE_BAD_FUNCTION_ARGUMENT / CURLE_NOT_BUILT_IN /
        /// CURLE_INTERFACE_FAILED / CURLE_RECURSIVE_API_CALL,以及 <c>curl_multi_*</c> 路径
        /// 返回非 0 的 CURLMcode(例如 add_handle / remove_handle 失败)。
        /// 这类多半是本库或 libcurl 自身的 bug,正常使用下几乎见不到。
        /// </summary>
        SetupFailed,
    }
}
