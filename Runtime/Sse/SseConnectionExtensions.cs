using System;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;

namespace CurlUnity.Sse
{
    /// <summary>
    /// Server-Sent Events Layer 2 入口：在 Layer 1 之上提供带自动重连/指数退避/心跳/状态机/
    /// <c>Last-Event-ID</c> 的便利层（<see cref="ISseConnection"/>）。重连/退避/心跳属工程策略，
    /// 不在协议核心（Layer 0/1）；只需单连接原语时用 <see cref="SseCoreExtensions.ReadServerSentEventsAsync"/>。
    /// </summary>
    public static class SseConnectionExtensions
    {
        /// <summary>
        /// 打开一个带自动重连的 SSE 连接（静态请求）。构造即开始连接；回调在<b>后台线程</b>触发。
        /// <b>请在本方法返回后立即挂回调</b>（OnEvent/OnError/OnStateChanged），首个事件/状态在其后才触发。
        /// 库内部每轮克隆 <paramref name="request"/> 并注入 Accept/Last-Event-ID，绝不改动该对象。
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="request"/> 已设置 <see cref="IHttpRequest.OnDataReceived"/>（SSE 需接管）。
        /// </exception>
        public static ISseConnection OpenSse(this IHttpClient client, IHttpRequest request,
            SseConnectionOptions options = null, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.OnDataReceived != null)
                throw new InvalidOperationException(
                    "SSE 需接管响应流式回调；请勿在传入的 request 上设置 OnDataReceived。");
            return new SseConnection(client, _ => Task.FromResult(request), options, ct);
        }

        /// <summary>
        /// 打开一个带自动重连的 SSE 连接，每轮(重)连前调用 <paramref name="requestFactory"/> 构造请求
        /// ——可在其中 <c>await</c> 刷新 token / 动态构造 URL、headers（async headers 外置，库不碰 token）。
        /// 工厂返回的 request 不应设置 <see cref="IHttpRequest.OnDataReceived"/>（由 SSE 接管，已设会经
        /// <see cref="ISseConnection.OnError"/> 报错并重连）。其余同 <see cref="OpenSse(IHttpClient, IHttpRequest, SseConnectionOptions, CancellationToken)"/>。
        /// </summary>
        public static ISseConnection OpenSse(this IHttpClient client,
            Func<CancellationToken, Task<IHttpRequest>> requestFactory,
            SseConnectionOptions options = null, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (requestFactory == null) throw new ArgumentNullException(nameof(requestFactory));
            return new SseConnection(client, requestFactory, options, ct);
        }
    }
}
