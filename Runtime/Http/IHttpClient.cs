using System;
using System.Threading;
using System.Threading.Tasks;

namespace CurlUnity.Http
{
    /// <summary>
    /// HTTP 客户端接口。负责接收 <see cref="IHttpRequest"/> 并异步返回 <see cref="IHttpResponse"/>,
    /// 同时作为 cookie jar、代理等跨请求状态的宿主。典型实现是 <see cref="CurlHttpClient"/>。
    /// </summary>
    /// <remarks>
    /// 单个实例线程安全,可在多个线程并发调用 <see cref="SendAsync"/>。实例通常长期存活
    /// (类似 <c>System.Net.Http.HttpClient</c>),不应为每次请求创建新 client。
    /// </remarks>
    public interface IHttpClient : IDisposable
    {
        /// <summary>
        /// 异步发送一个 HTTP 请求。Task 完成后的结果所有权归调用方,用完需 <c>Dispose()</c>
        /// 释放底层 easy handle。
        /// </summary>
        /// <param name="request">请求配置。不可为 null,<see cref="IHttpRequest.Url"/> 必填。</param>
        /// <param name="ct">取消 token。请求已在飞行时取消会把 Task 置为 Canceled 并向
        /// libcurl 发取消,不等待网络。</param>
        Task<IHttpResponse> SendAsync(IHttpRequest request, CancellationToken ct = default);

        /// <summary>启用代理。对本次调用之后构建的请求生效，不影响已进入 worker 队列的请求。</summary>
        void SetProxy(HttpProxy proxy);

        /// <summary>关闭代理，回到 client 的默认行为（不走代理，且屏蔽环境变量）。</summary>
        void ClearProxy();
    }
}
