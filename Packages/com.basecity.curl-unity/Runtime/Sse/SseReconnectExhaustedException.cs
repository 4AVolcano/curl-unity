using System;

namespace CurlUnity.Sse
{
    /// <summary>
    /// SSE 自动重连达到配置上限（<see cref="SseConnectionOptions.MaxReconnectAttempts"/> 或
    /// <see cref="SseConnectionOptions.MaxElapsedReconnectTime"/>）而放弃时，通过
    /// <see cref="ISseConnection.Completion"/> 以本异常 fault 暴露。
    /// <see cref="Exception.InnerException"/> 为最后一次失败的原因（干净 2xx EOF 触发的放弃为 <c>null</c>）。
    /// </summary>
    public sealed class SseReconnectExhaustedException : Exception
    {
        /// <summary>放弃前连续失败的次数。</summary>
        public int AttemptCount { get; }

        public SseReconnectExhaustedException(int attemptCount, Exception lastError)
            : base($"SSE 自动重连在 {attemptCount} 次连续失败后放弃。", lastError)
        {
            AttemptCount = attemptCount;
        }
    }
}
