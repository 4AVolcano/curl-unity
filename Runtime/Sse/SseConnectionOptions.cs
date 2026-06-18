using System;
using System.Threading;
using System.Threading.Tasks;

namespace CurlUnity.Sse
{
    /// <summary>
    /// Layer 2 重连便利层（<see cref="SseConnectionExtensions.OpenSse"/>）的策略配置。
    /// 只含工程策略字段，<b>不含请求字段</b>——请求仍用 <see cref="Http.IHttpRequest"/>。
    /// <para>
    /// 所有字段的默认值组合等价于 <c>EventSource</c> 风格的<b>无限重连</b>：固定基准退避（默认
    /// <c>t*2</c> clamp 到 [1s,32s]）、不抖动、不封顶次数/时长、收到任意字节即视为已建立并重置退避。
    /// 仅在显式配置对应字段时才启用 jitter / 次数 / 时长 / 自定义判定等生产护栏。
    /// </para>
    /// </summary>
    public sealed class SseConnectionOptions
    {
        // ——————————————————————————— 退避 ———————————————————————————

        /// <summary>首次重连延迟，也是退避重置后的基准值。默认 1s。<see cref="ReconnectDelayInit"/> 必须 ≥ 0。</summary>
        public TimeSpan ReconnectDelayInit { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 退避递增函数：输入上次延迟、返回下次延迟。默认 <c>t*2</c> 并 clamp 到 [1s, 32s]
        /// （用 <c>Math.Min/Max</c>，不依赖 <c>Math.Clamp</c> 以兼容 netstandard2.1）。
        /// <b>仅在连接"未建立"（见 <see cref="BackoffResetThreshold"/>）时递增</b>；已建立连接断开后退避重置回
        /// <see cref="ReconnectDelayInit"/>。服务端 <c>retry:</c> 字段（若有）优先于本函数（见
        /// <see cref="SseEventParser.RetryMilliseconds"/>），并同样受 <see cref="MaxReconnectDelay"/> /
        /// <see cref="ReconnectJitter"/> 约束。
        /// </summary>
        public Func<TimeSpan, TimeSpan> ReconnectDelayIncFn { get; set; }
            = t => TimeSpan.FromSeconds(Math.Min(Math.Max(t.TotalSeconds * 2, 1), 32));

        /// <summary>
        /// 连接自收到首字节起需保持至少这么久，断开后才把退避重置回 <see cref="ReconnectDelayInit"/>。
        /// 默认 <see cref="TimeSpan.Zero"/>：收到<b>任意字节</b>即视为"已建立"（EventSource 风格）。
        /// <para>
        /// 把它设为正值可防御"建连即断"的抖动服务端——此类连接虽然收到过字节但存活不足阈值，
        /// <b>不</b>被视为已建立，退避因而持续递增，避免以基准间隔无限热循环猛打服务端。
        /// 完全未收到字节的空 2xx EOF 永远不算已建立（与本阈值无关）。
        /// </para>
        /// </summary>
        public TimeSpan BackoffResetThreshold { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// 重连等待的上限——封顶递增退避、服务端 <c>retry:</c> 与抖动后的<b>最终</b>等待时长。
        /// <c>null</c>（默认）= 不额外封顶（递增退避仍受 <see cref="ReconnectDelayIncFn"/> 自身的 clamp 约束）。
        /// 设置时必须为正。
        /// </summary>
        public TimeSpan? MaxReconnectDelay { get; set; }

        /// <summary>
        /// 重连延迟抖动系数，取值 <c>[0,1]</c>。启用后实际等待在 <c>[delay*(1-Jitter), delay]</c> 区间内
        /// 均匀随机（只下调、不超过基准，故不会突破 <see cref="MaxReconnectDelay"/>），用于打散大量客户端
        /// 同时重连造成的"重连风暴"。0（默认）= 不抖动。
        /// </summary>
        public double ReconnectJitter { get; set; }

        // ————————————————————— 放弃策略（默认不限）—————————————————————

        /// <summary>
        /// 连续失败达到该次数即放弃重连（含首连失败）；放弃时 <see cref="ISseConnection.Completion"/> 以
        /// <see cref="SseReconnectExhaustedException"/> fault。计数在连接<b>成功建立</b>后清零。
        /// 0（默认）= 不限。
        /// </summary>
        public int MaxReconnectAttempts { get; set; }

        /// <summary>
        /// 自首次失败起、连续重连累计耗时超过该值即放弃重连（语义同 <see cref="MaxReconnectAttempts"/>，
        /// 以时长代替次数）。计时在连接<b>成功建立</b>后清零。<c>null</c>（默认）= 不限；设置时必须为正。
        /// </summary>
        public TimeSpan? MaxElapsedReconnectTime { get; set; }

        /// <summary>
        /// 自定义"是否重连"判定。入参为触发本次重连的异常——网络/TLS <c>CurlHttpException</c>、
        /// 非 2xx <see cref="SseHttpStatusException"/>、空闲超时 <see cref="System.TimeoutException"/>，
        /// 或干净 2xx EOF（<c>null</c>）。返回 <c>false</c> 即停止重连并让 <see cref="ISseConnection.Completion"/>
        /// 优雅完成。<c>null</c>（默认）= 始终重连。
        /// <para><b>204 No Content 恒为终止，不经过本回调。</b></para>
        /// </summary>
        public Func<Exception, bool> ShouldReconnect { get; set; }

        // ——————————————————————— 心跳 / 空闲 ———————————————————————

        /// <summary>
        /// 空闲/心跳超时：超过此时长未收到任何字节（含注释心跳）则判定连接僵死并重连。
        /// <c>null</c>（默认）= 不启用；设置时必须为正。
        /// </summary>
        public TimeSpan? IdleTimeout { get; set; }

        // ——————————————————————— Last-Event-ID ———————————————————————

        /// <summary>重连时是否自动注入 <c>Last-Event-ID</c>（取自解析器已确认的 id）。默认 <c>true</c>（SSE 规范行为）。</summary>
        public bool AutoInjectLastEventId { get; set; } = true;

        // ————————————————— 测试注入点（internal，不属公开 API）—————————————————

        /// <summary>
        /// 抖动随机源，返回 <c>[0,1)</c>。<c>null</c>（默认）= 每个连接用一个去相关种子的 <see cref="Random"/>。
        /// 仅供单测注入确定性随机，故为 <c>internal</c>。
        /// </summary>
        internal Func<double> JitterRng { get; set; }

        /// <summary>
        /// 重连等待的底层实现 <c>(delay, ct) =&gt; Task</c>。<c>null</c>（默认）= <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。
        /// 仅供单测在不真实等待的前提下观测计算出的延迟，故为 <c>internal</c>。
        /// </summary>
        internal Func<TimeSpan, CancellationToken, Task> DelayProvider { get; set; }
    }
}
