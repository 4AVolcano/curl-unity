using System;
using System.Collections.Generic;
using System.IO;

namespace CurlUnity.Http
{
    /// <summary>
    /// 一次 HTTP 请求的配置。交给 <see cref="IHttpClient.SendAsync"/> 后由 client 解读
    /// 成对应的 libcurl easy handle 选项。典型实现是 <see cref="HttpRequest"/>,按 POCO
    /// 字段赋值即可;认证等扩展方法见 <see cref="HttpRequestExtensions"/>。
    /// </summary>
    public interface IHttpRequest
    {
        /// <summary>HTTP 方法。默认 <see cref="HttpMethod.Get"/>。</summary>
        HttpMethod Method { get; set; }

        /// <summary>请求 URL (scheme + host + path + query)。必填,不可为 null。</summary>
        string Url { get; set; }

        /// <summary>
        /// 自定义请求头。允许重复 key,libcurl 会把同名 header 按集合顺序合并送出。
        /// 设置 <c>User-Agent</c> 会覆盖 <see cref="CurlHttpClient.UserAgent"/>。
        /// </summary>
        IEnumerable<KeyValuePair<string, string>> Headers { get; set; }

        /// <summary>
        /// 请求体 raw bytes。与 <see cref="BodyStream"/> 互斥,同时设置会在 Send 时 throw。
        /// JSON / form-urlencoded / multipart 等常见 body 可用
        /// <see cref="HttpClientExtensions"/> 的便利方法构造。
        /// </summary>
        byte[] Body { get; set; }

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
        Stream BodyStream { get; set; }

        /// <summary>
        /// <see cref="BodyStream"/> 的总长度。
        /// <list type="bullet">
        ///   <item>非 <c>null</c>: 设置 <c>Content-Length</c> header,libcurl 按 fixed-length 上传</item>
        ///   <item><c>null</c>: 长度未知,libcurl 使用 <c>Transfer-Encoding: chunked</c></item>
        /// </list>
        /// 对 <c>MemoryStream</c> / <c>FileStream</c> 这类可 seek 的 Stream,传
        /// <c>stream.Length - stream.Position</c> 是常见做法。
        /// </summary>
        long? BodyLength { get; set; }

        /// <summary>TCP 建连超时（毫秒），0 = 不限</summary>
        int ConnectTimeoutMs { get; set; }

        /// <summary>整个请求响应超时（毫秒），0 = 不限</summary>
        int TimeoutMs { get; set; }

        /// <summary>是否捕获响应头。默认 false。</summary>
        bool EnableResponseHeaders { get; set; }

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
        bool AutoDecompressResponse { get; set; }

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
        bool EnableCookies { get; set; }

        /// <summary>
        /// 流式数据回调（文件下载等场景）。
        /// 设置后响应体不缓冲，数据逐块交付，Response.Body 为 null。
        /// 在后台线程调用。参数: (buffer, offset, length)
        /// </summary>
        /// <remarks>
        /// <b>契约：回调必须快速返回。</b> 该回调在 libcurl 的 write function
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
        /// </remarks>
        Action<byte[], int, int> OnDataReceived { get; set; }
    }
}
