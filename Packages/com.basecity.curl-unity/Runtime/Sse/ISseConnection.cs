using System;
using System.Threading.Tasks;

namespace CurlUnity.Sse
{
    /// <summary>
    /// 一个带自动重连的 SSE 连接句柄（Layer 2）。由 <see cref="SseConnectionExtensions.OpenSse"/> 创建，
    /// 构造即开始连接；<see cref="IDisposable.Dispose"/> 关闭并停止重连。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>所有回调在后台线程触发（worker / 线程池），禁止阻塞</b>；需要碰 Unity API 时调用方自行 marshal
    /// （与 <see cref="Http.IHttpRequest.OnDataReceived"/> 契约一致）。
    /// </para>
    /// <para>
    /// <b>回调（<see cref="OnEvent"/> / <see cref="OnError"/> / <see cref="OnStateChanged"/>）不应抛异常</b>：
    /// 一旦某个回调抛出，连接会终止，且该异常通过 <see cref="Completion"/> fault 暴露给调用方（不会静默丢弃，
    /// 也不会触发重连风暴）。
    /// </para>
    /// <para>
    /// 想在构造阶段就消除 construct-then-subscribe 竞态、保证不漏掉首个事件/状态时，请用
    /// <see cref="SseConnectionExtensions.OpenSse(Http.IHttpClient, Http.IHttpRequest, Action{SseEvent}, Action{Exception}, Action{SseConnectionState, SseConnectionState}, SseConnectionOptions, System.Threading.CancellationToken)"/>
    /// 在打开时直接传入回调（回调在后台循环启动前完成挂接）。
    /// </para>
    /// </remarks>
    public interface ISseConnection : IDisposable
    {
        /// <summary>当前连接状态。</summary>
        SseConnectionState State { get; }

        /// <summary>
        /// 后台重连循环的终止信号，可 <c>await</c> 以感知连接彻底结束并区分结束原因：
        /// <list type="bullet">
        ///   <item><b>RanToCompletion</b>：优雅结束——<see cref="IDisposable.Dispose"/>、外部取消、
        ///   收到 <c>204</c>、或 <see cref="SseConnectionOptions.ShouldReconnect"/> 返回 <c>false</c>。</item>
        ///   <item><b>Faulted</b>（<see cref="SseReconnectExhaustedException"/>）：达到
        ///   <see cref="SseConnectionOptions.MaxReconnectAttempts"/> /
        ///   <see cref="SseConnectionOptions.MaxElapsedReconnectTime"/> 上限而放弃。</item>
        ///   <item><b>Faulted</b>（原始异常）：某个用户回调抛出，或后台循环遇到未预期异常。</item>
        /// </list>
        /// 单次连接的失败<b>不会</b>完成本任务（仍会重连，错误经 <see cref="OnError"/> 报告）。
        /// </summary>
        Task Completion { get; }

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
