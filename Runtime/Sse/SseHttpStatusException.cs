using System;

namespace CurlUnity.Sse
{
    /// <summary>
    /// SSE 连接收到非 2xx HTTP 状态码时，经 <see cref="ISseConnection.OnError"/> 传递。
    /// </summary>
    public sealed class SseHttpStatusException : Exception
    {
        /// <summary>服务端返回的 HTTP 状态码。</summary>
        public int StatusCode { get; }

        public SseHttpStatusException(int statusCode)
            : base($"SSE 连接返回非 2xx 状态码: {statusCode}")
        {
            StatusCode = statusCode;
        }
    }
}
