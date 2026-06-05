using System;

namespace CurlUnity.Sse
{
    /// <summary>
    /// Layer 2 重连便利层（<see cref="SseConnectionExtensions.OpenSse"/>）的策略配置。
    /// 只含工程策略字段，<b>不含请求字段</b>——请求仍用 <see cref="Http.IHttpRequest"/>。
    /// </summary>
    public sealed class SseConnectionOptions
    {
        /// <summary>首次重连延迟。每次连接成功（收到首字节）后重置为此值。默认 1s。</summary>
        public TimeSpan ReconnectDelayInit { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 退避递增函数：输入上次延迟、返回下次延迟。默认 <c>t*2</c> 并 clamp 到 [1s, 32s]
        /// （用 <c>Math.Min/Max</c>，不依赖 <c>Math.Clamp</c> 以兼容 netstandard2.1）。
        /// 服务端 <c>retry:</c> 字段（若有）优先于本函数。
        /// </summary>
        public Func<TimeSpan, TimeSpan> ReconnectDelayIncFn { get; set; }
            = t => TimeSpan.FromSeconds(Math.Min(Math.Max(t.TotalSeconds * 2, 1), 32));

        /// <summary>
        /// 空闲/心跳超时：超过此时长未收到任何字节（含注释心跳）则判定连接僵死并重连。
        /// <c>null</c>（默认）= 不启用。
        /// </summary>
        public TimeSpan? IdleTimeout { get; set; }

        /// <summary>重连时是否自动注入 <c>Last-Event-ID</c>（取自解析器已确认的 id）。默认 <c>true</c>（SSE 规范行为）。</summary>
        public bool AutoInjectLastEventId { get; set; } = true;
    }
}
