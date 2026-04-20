using System;
using System.Collections.Generic;

namespace CurlUnity.Http
{
    public interface IHttpRequest
    {
        HttpMethod Method { get; set; }
        string Url { get; set; }
        IEnumerable<KeyValuePair<string, string>> Headers { get; set; }
        byte[] Body { get; set; }

        /// <summary>TCP 建连超时（毫秒），0 = 不限</summary>
        int ConnectTimeoutMs { get; set; }

        /// <summary>整个请求响应超时（毫秒），0 = 不限</summary>
        int TimeoutMs { get; set; }

        /// <summary>是否捕获响应头。默认 false。</summary>
        bool EnableResponseHeaders { get; set; }

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
