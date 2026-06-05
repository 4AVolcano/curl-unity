using System;

namespace CurlUnity.Sse
{
    /// <summary>
    /// 一个带自动重连的 SSE 连接句柄（Layer 2）。由 <see cref="SseConnectionExtensions.OpenSse"/> 创建，
    /// 构造即开始连接；<see cref="IDisposable.Dispose"/> 关闭并停止重连。
    /// </summary>
    /// <remarks>
    /// <b>所有回调在后台线程触发（worker / 线程池），禁止阻塞</b>；需要碰 Unity API 时调用方自行 marshal
    /// （与 <see cref="Http.IHttpRequest.OnDataReceived"/> 契约一致）。回调内抛出的异常不被本层吞掉。
    /// </remarks>
    public interface ISseConnection : IDisposable
    {
        /// <summary>当前连接状态。</summary>
        SseConnectionState State { get; }

        /// <summary>每解析出一个完整事件触发。后台线程。</summary>
        event Action<SseEvent> OnEvent;

        /// <summary>
        /// 连接错误触发（随后自动重连）。后台线程。常见类型：网络/TLS/超时 <c>CurlHttpException</c>、
        /// 非 2xx <see cref="SseHttpStatusException"/>、空闲超时 <see cref="System.TimeoutException"/>。
        /// </summary>
        event Action<Exception> OnError;

        /// <summary>状态变化触发，参数为 (旧状态, 新状态)。后台线程。</summary>
        event Action<SseConnectionState, SseConnectionState> OnStateChanged;
    }
}
