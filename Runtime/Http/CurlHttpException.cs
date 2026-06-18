using System;

namespace CurlUnity.Http
{
    /// <summary>
    /// <see cref="IHttpClient.SendAsync"/> 在**网络/协议**层面失败时抛出的异常。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本异常的适用范围</b>:仅用于"libcurl 报告了错误"的场景 —— 连接/TLS/超时/协议错、
    /// 或 curl_multi_* 路径的返回码非 0。使用者应按 <see cref="ErrorKind"/> 做分支,不要
    /// 基于 <see cref="CurlCode"/> 的具体数值,后者只用于日志和排查。
    /// </para>
    /// <para>
    /// <b>不经过本异常的失败路径</b>(故意区分开,不要混处理):
    /// <list type="bullet">
    ///   <item><description>
    ///     用户主动取消 <see cref="System.Threading.CancellationToken"/> 或在请求进行中
    ///     <c>Dispose</c> 掉 <c>CurlHttpClient</c>: 抛 <see cref="OperationCanceledException"/>。
    ///   </description></item>
    ///   <item><description>
    ///     用户写的流式上传 <c>Stream.Read</c> 或下载 <c>DataCallback</c> 抛出的异常: 原样透传
    ///     (保留原始栈),根因在用户代码,不包装。
    ///   </description></item>
    ///   <item><description>
    ///     用法/编程错误(null 参数、已 Dispose 的 client、Body/BodyStream 互斥冲突等):
    ///     抛原生 <see cref="ArgumentException"/> / <see cref="ObjectDisposedException"/> /
    ///     <see cref="InvalidOperationException"/>,反映这是 bug 而非运行时失败。
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>HTTP 4xx/5xx 不算失败</b>。只要拿到了响应,<c>SendAsync</c> 就正常返回
    /// <see cref="IHttpResponse"/>;业务侧靠 <see cref="IHttpResponse.StatusCode"/> 判断。
    /// </para>
    /// </remarks>
    public sealed class CurlHttpException : Exception
    {
        /// <summary>语义化分类,做 switch/if 就读这个字段。</summary>
        public HttpErrorKind ErrorKind { get; }

        /// <summary>
        /// libcurl 返回的原始数值,给日志/issue 排查用。多数场景下是 easy 层的 <c>CURLcode</c>,
        /// 但 <see cref="HttpErrorKind.SetupFailed"/> 里发生在 <c>curl_multi_*</c> 路径时为
        /// <c>CURLMcode</c>。两者数值空间不同(如数值 2 在 easy 层是 <c>CURLE_FAILED_INIT</c>,
        /// 在 multi 层是 <c>CURLM_OUT_OF_MEMORY</c>),区分靠 <see cref="ErrorKind"/>。
        /// </summary>
        public int CurlCode { get; }

        internal CurlHttpException(HttpErrorKind kind, int curlCode, string nativeMessage)
            : base(BuildMessage(kind, curlCode, nativeMessage))
        {
            ErrorKind = kind;
            CurlCode = curlCode;
        }

        internal static CurlHttpException FromEasyCode(int curlCode, string nativeMessage)
            => new CurlHttpException(MapEasyCode(curlCode), curlCode, nativeMessage);

        /// <summary>
        /// 本库 <c>curl_multi_*</c> / setup 路径失败构造异常;统一归类到
        /// <see cref="HttpErrorKind.SetupFailed"/>。<paramref name="curlCode"/> 可能是
        /// <c>CURLMcode</c> 也可能是 easy 层 <c>CURLcode</c>(例如 setopt 失败在 build 后
        /// 阶段的补配)。
        /// </summary>
        internal static CurlHttpException SetupFailure(int curlCode, string nativeMessage)
            => new CurlHttpException(HttpErrorKind.SetupFailed, curlCode, nativeMessage);

        /// <summary>
        /// 将 libcurl easy 层 <c>CURLcode</c> 映射到 <see cref="HttpErrorKind"/>。
        /// 数值引用自 <c>curl/curl.h</c>(libcurl 严格保证向后兼容,数值永远不变)。
        /// 未列出的代码(FTP/LDAP/TFTP/SSH/RTSP 专用、未来新增等)一律归到 <see cref="HttpErrorKind.Unknown"/>。
        /// </summary>
        internal static HttpErrorKind MapEasyCode(int curlCode)
        {
            switch (curlCode)
            {
                // --- InvalidUrl ---
                case 1:  // CURLE_UNSUPPORTED_PROTOCOL
                case 3:  // CURLE_URL_MALFORMAT
                    return HttpErrorKind.InvalidUrl;

                // --- DnsFailed ---
                case 5:  // CURLE_COULDNT_RESOLVE_PROXY
                case 6:  // CURLE_COULDNT_RESOLVE_HOST
                    return HttpErrorKind.DnsFailed;

                // --- ConnectFailed ---
                case 7:  // CURLE_COULDNT_CONNECT
                    return HttpErrorKind.ConnectFailed;

                // --- TlsError ---
                case 35: // CURLE_SSL_CONNECT_ERROR
                case 53: // CURLE_SSL_ENGINE_NOTFOUND
                case 54: // CURLE_SSL_ENGINE_SETFAILED
                case 58: // CURLE_SSL_CERTPROBLEM
                case 59: // CURLE_SSL_CIPHER
                case 60: // CURLE_PEER_FAILED_VERIFICATION
                case 64: // CURLE_USE_SSL_FAILED
                case 66: // CURLE_SSL_ENGINE_INITFAILED
                case 77: // CURLE_SSL_CACERT_BADFILE
                case 80: // CURLE_SSL_SHUTDOWN_FAILED
                case 82: // CURLE_SSL_CRL_BADFILE
                case 83: // CURLE_SSL_ISSUER_ERROR
                case 90: // CURLE_SSL_PINNEDPUBKEYNOTMATCH
                case 91: // CURLE_SSL_INVALIDCERTSTATUS
                case 98: // CURLE_SSL_CLIENTCERT
                    return HttpErrorKind.TlsError;

                // --- Timeout ---
                case 28: // CURLE_OPERATION_TIMEDOUT
                    return HttpErrorKind.Timeout;

                // --- NetworkIo ---
                case 18: // CURLE_PARTIAL_FILE
                case 52: // CURLE_GOT_NOTHING
                case 55: // CURLE_SEND_ERROR
                case 56: // CURLE_RECV_ERROR
                case 65: // CURLE_SEND_FAIL_REWIND
                case 81: // CURLE_AGAIN
                    return HttpErrorKind.NetworkIo;

                // --- ProtocolError ---
                case 8:  // CURLE_WEIRD_SERVER_REPLY
                case 16: // CURLE_HTTP2
                case 61: // CURLE_BAD_CONTENT_ENCODING
                case 88: // CURLE_CHUNK_FAILED
                case 92: // CURLE_HTTP2_STREAM
                case 95: // CURLE_HTTP3
                case 96: // CURLE_QUIC_CONNECT_ERROR
                    return HttpErrorKind.ProtocolError;

                // --- ProxyError ---
                case 97: // CURLE_PROXY
                    return HttpErrorKind.ProxyError;

                // --- TooManyRedirects ---
                case 47: // CURLE_TOO_MANY_REDIRECTS
                    return HttpErrorKind.TooManyRedirects;

                // --- OutOfMemory ---
                case 27: // CURLE_OUT_OF_MEMORY
                    return HttpErrorKind.OutOfMemory;

                // --- SetupFailed ---
                case 2:  // CURLE_FAILED_INIT
                case 4:  // CURLE_NOT_BUILT_IN
                case 43: // CURLE_BAD_FUNCTION_ARGUMENT
                case 45: // CURLE_INTERFACE_FAILED
                case 48: // CURLE_UNKNOWN_OPTION
                case 49: // CURLE_SETOPT_OPTION_SYNTAX
                case 93: // CURLE_RECURSIVE_API_CALL
                    return HttpErrorKind.SetupFailed;

                default:
                    // 未分类(含 FTP/LDAP/TFTP/SSH/RTSP 专用、ABORTED_BY_CALLBACK/WRITE_ERROR/
                    // READ_ERROR 这几个在本库里被用户回调异常 rethrow 覆盖的残余、未来 libcurl
                    // 新增的 code 等)都走 Unknown。CurlCode 字段仍保留原始值供排查。
                    return HttpErrorKind.Unknown;
            }
        }

        private static string BuildMessage(HttpErrorKind kind, int curlCode, string nativeMessage)
        {
            return string.IsNullOrEmpty(nativeMessage)
                ? $"[{kind}] curl({curlCode})"
                : $"[{kind}] curl({curlCode}): {nativeMessage}";
        }
    }
}
