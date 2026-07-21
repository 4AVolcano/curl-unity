using System;
using System.Collections.Generic;
using System.IO;

namespace CurlUnity.Http
{
    /// <summary>
    /// 一次 HTTP 请求的配置。交给 <see cref="IHttpClient.SendAsync"/> 后由 client 解读
    /// 成对应的 libcurl easy handle 选项。按 POCO 字段赋值即可；认证等扩展方法见
    /// <see cref="HttpRequestExtensions"/>。
    /// </summary>
    public class HttpRequest
    {
        /// <summary>HTTP 方法。默认 <see cref="HttpMethod.Get"/>。</summary>
        public HttpMethod Method { get; set; } = HttpMethod.Get;

        /// <summary>请求 URL (scheme + host + path + query)。必填,不可为 null。</summary>
        public string Url { get; set; }

        /// <summary>
        /// 自定义请求头。允许重复 key,libcurl 会把同名 header 按集合顺序合并送出。
        /// 设置 <c>User-Agent</c> 会覆盖 <see cref="CurlHttpClient.UserAgent"/>。
        /// </summary>
        public IEnumerable<KeyValuePair<string, string>> Headers { get; set; }

        /// <summary>
        /// 请求体 raw bytes。与 <see cref="BodyStream"/> 互斥,同时设置会在 Send 时 throw。
        /// JSON / form-urlencoded / multipart 等常见 body 可用
        /// <see cref="HttpClientExtensions"/> 的便利方法构造。
        /// </summary>
        public byte[] Body { get; set; }

        /// <summary>
        /// 流式请求体。非 <c>null</c> 时以流式上传,request 期间 libcurl 按需从该 Stream 读数据;
        /// 与 <see cref="Body"/> 互斥(同时设置会 throw);仅支持 POST/PUT/PATCH 等带 body 的方法。
        /// </summary>
        /// <remarks>
        /// <para>
        /// Stream 生命周期归调用方,本库不会 Dispose;请求发出到完成期间 Stream 必须可读且不关闭。
        /// </para>
        /// <para>
        /// <b>不支持 rewind</b>:若 server 返回 3xx 重定向或 HTTP 认证挑战导致 libcurl 需要重发 body,
        /// 请求会失败(未注册 <c>CURLOPT_SEEKFUNCTION</c>)。上传场景此类情况罕见。
        /// </para>
        /// </remarks>
        public Stream BodyStream { get; set; }

        /// <summary>
        /// <see cref="BodyStream"/> 的总长度。
        /// <list type="bullet">
        ///   <item>非 <c>null</c>: 设置 <c>Content-Length</c> header,libcurl 按 fixed-length 上传</item>
        ///   <item><c>null</c>: 长度未知,libcurl 使用 <c>Transfer-Encoding: chunked</c></item>
        /// </list>
        /// 对 <c>MemoryStream</c> / <c>FileStream</c> 这类可 seek 的 Stream,传
        /// <c>stream.Length - stream.Position</c> 是常见做法。
        /// </summary>
        public long? BodyLength { get; set; }

        /// <summary>
        /// TCP 建连超时（毫秒），0 = 沿用 libcurl 默认（300 秒）。
        /// 默认 30000（30 秒）——移动网络下 300 秒的建连等待等同于挂死。
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 30_000;

        /// <summary>
        /// 整个请求响应超时（毫秒），0 = 不限（默认）。长下载等场景不宜设置整体
        /// 超时；如需检测传输中途僵死的连接（NAT 静默断链、Wi-Fi/蜂窝切换），
        /// 请配合 <see cref="LowSpeedLimitBytesPerSecond"/> / <see cref="LowSpeedTimeSeconds"/>。
        /// </summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// 低速检测阈值（bytes/s）。与 <see cref="LowSpeedTimeSeconds"/> 成对启用：
        /// 传输速率低于该值持续指定秒数后，请求以超时失败
        /// （<c>HttpErrorKind.Timeout</c>）。0（默认）= 不启用。
        /// 两者必须同时为正或同时为 0，只设其一会在 Send 时抛异常。
        /// 典型取值：limit=1, time=60 —— 60 秒一个字节都没收/发到才判死，
        /// 不会误杀慢速但活着的传输。
        /// </summary>
        public int LowSpeedLimitBytesPerSecond { get; set; }

        /// <summary>低速持续秒数，见 <see cref="LowSpeedLimitBytesPerSecond"/>。0（默认）= 不启用。</summary>
        public int LowSpeedTimeSeconds { get; set; }

        /// <summary>
        /// 是否自动跟随 3xx 重定向。默认 <c>true</c>。
        /// <para>
        /// <b>安全提示</b>：libcurl 跟随重定向时会把自定义 header（含
        /// <c>Authorization</c>）原样发给重定向目标，包括跨主机目标。请求带凭据
        /// 且目标 URL 不完全可信时，建议置 <c>false</c> 自行处理 3xx。
        /// </para>
        /// </summary>
        public bool FollowRedirects { get; set; } = true;

        /// <summary>
        /// 跟随重定向的最大跳数，仅 <see cref="FollowRedirects"/> 为 <c>true</c> 时生效。
        /// 默认 30（与 libcurl 8.3+ 默认一致）。0 = 拒绝任何重定向（首跳即失败），
        /// -1 = 不限制（不推荐）。超限时请求以 <c>CURLE_TOO_MANY_REDIRECTS</c> 失败。
        /// </summary>
        public int MaxRedirects { get; set; } = 30;

        /// <summary>是否捕获响应头。默认 false。</summary>
        public bool EnableResponseHeaders { get; set; }

        /// <summary>
        /// 是否让 libcurl 自动处理响应压缩。默认 <c>true</c>。
        /// <para>
        /// 为 <c>true</c>:发送 <c>Accept-Encoding: gzip, deflate</c>(按编译时链接的
        /// 压缩库), libcurl 自动解压响应, <c>HttpResponse.Body</c> 拿到的是解压后的原文。
        /// 对 JSON/HTML/text 可降低 3-5 倍下行流量。
        /// </para>
        /// <para>
        /// 为 <c>false</c>:不发 <c>Accept-Encoding</c>, 响应按 server 原样交付;
        /// 如果 server 仍回 <c>Content-Encoding: gzip</c>, Body 是压缩字节, 需调用方自理。
        /// </para>
        /// </summary>
        public bool AutoDecompressResponse { get; set; } = true;

        /// <summary>
        /// 是否接入所属 <see cref="IHttpClient"/> 的共享 cookie jar。默认 false。
        /// <para>
        /// 为 <c>true</c> 时：服务端 <c>Set-Cookie</c> 写入 jar、后续请求自动回发匹配 cookie，
        /// 在 <b>同一个 <see cref="IHttpClient"/> 实例</b> 内跨请求持久化。
        /// 不同 client 实例的 jar 互相独立。
        /// </para>
        /// <para>
        /// 为 <c>false</c> 时：cookie engine 完全不启用 —— 本次请求既不读 jar 也不写 jar
        /// （即便 client 的 jar 已有条目也不会带出），且同一请求 redirect 链内的 <c>Set-Cookie</c>
        /// 也不会被解析回发。需要这两种行为之一时请置 <c>true</c>。
        /// </para>
        /// <para>
        /// jar 为纯内存存储，client Dispose 后清空；暂不支持文件持久化。
        /// </para>
        /// </summary>
        public bool EnableCookies { get; set; }

        /// <summary>
        /// 流式数据回调（文件下载等场景）。
        /// 设置后响应体不缓冲，数据逐块交付，Response.Body 为 null。
        /// 在后台线程调用。参数: (buffer, offset, length)
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>契约：buffer 仅在本次回调执行期间有效。</b> buffer 来自共享池，回调返回后
        /// 其内容可能立即被后续网络数据覆盖。同步写文件、同步计算 hash、同步解析均可直接使用；
        /// 如需保存、排队或跨线程处理，必须在回调内复制有效的 <c>buffer</c> 区间。
        /// </para>
        /// <para>
        /// <b>回调必须快速返回。</b> 该回调在 libcurl 的 write function
        /// 调用栈内执行，libcurl 不允许中断进行中的回调；在回调里阻塞会直接
        /// 占住 worker 线程：
        /// <list type="bullet">
        ///   <item>同一 <see cref="IHttpClient"/> 上其它请求的 I/O 推进会被延迟；</item>
        ///   <item>Dispose 时 worker 线程无法及时退出；超过内部超时后 Dispose 会
        ///   <b>跳过 curl_multi_cleanup</b>（记一条错误日志），让 multi handle
        ///   交由 OS 在进程退出时回收，以免与仍在执行的回调发生 use-after-free。</item>
        /// </list>
        /// 需要长时间处理数据时，回调里把 buffer 拷走投递到别的线程即可，不要在
        /// 回调里同步等 I/O、锁或其它长耗时工作。
        /// </para>
        /// </remarks>
        public Action<byte[], int, int> OnDataReceived { get; set; }

        /// <summary>
        /// 最终响应头就绪回调。最终响应的 header block 完整结束后调用一次，且先于任何
        /// body 数据交付；不需要等待第一块 body，因此保持静默的流式响应也会触发。
        /// 回调参数是 <see cref="IHttpResponse"/>——与 <see cref="IHttpClient.SendAsync"/>
        /// 最终返回的是同一实例。回调时 <see cref="IHttpResponse.StatusCode"/>、
        /// <see cref="IHttpResponse.ContentType"/>、<see cref="IHttpResponse.Version"/> 等
        /// 通过 <c>curl_easy_getinfo</c> 读取的属性可用；<see cref="IHttpResponse.Body"/>
        /// 为 null；<see cref="IHttpResponse.Headers"/> 仅在 <see cref="EnableResponseHeaders"/>
        /// 为 true 时可用。
        /// </summary>
        /// <remarks>
        /// <para>在后台线程调用。回调抛异常等同于 <see cref="OnDataReceived"/> 抛异常——
        /// 中止传输，异常原样透传给 <c>SendAsync</c> Task。</para>
        /// <para>自动跟随重定向时，中间响应不会触发；仅最终响应触发一次。HEAD 请求或
        /// 204 等无 body 的响应同样会触发，触发不依赖 body 数据到达。</para>
        /// </remarks>
        public Action<IHttpResponse> OnHeadersReceived { get; set; }

        /// <summary>
        /// 连接及协议协商完成、请求即将发送时触发。内部传输事件，不对外暴露。
        /// 同一个逻辑请求在重定向等场景下可能触发多次。
        /// </summary>
        internal Action OnBeforeSendRequest { get; set; }

        /// <summary>
        /// 每次收到一段响应头数据时触发。内部传输事件，不对外暴露；可能对应状态行、
        /// 普通 header 行或 header block 的空白结束行，并且可能触发多次。
        /// </summary>
        internal Action OnHeaderReceived { get; set; }

        /// <summary>
        /// 是否启用 TCP keep-alive（<c>CURLOPT_TCP_KEEPALIVE</c>）。内部字段，不对外暴露；
        /// 目前仅 SSE 单连接读取（<c>ReadServerSentEventsAsync</c>）内部为长连接默认置 true。
        /// </summary>
        internal bool TcpKeepAlive { get; set; }
    }
}
