using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;

namespace CurlUnity.Sse
{
    /// <summary>
    /// Server-Sent Events 核心入口（Layer 1）：在一个 <see cref="IHttpRequest"/> 上读取一段
    /// SSE 连接，把响应体增量解析成 <see cref="SseEvent"/>，连接结束即返回。
    /// <b>不重连</b>——重连/退避/心跳等工程策略由上层组合（参见解析器 <see cref="SseEventParser"/>，
    /// 可与 <see cref="IHttpClient.SendAsync"/> 自行编排）。
    /// </summary>
    public static class SseCoreExtensions
    {
        private const string AcceptHeaderName = "Accept";
        private const string EventStreamContentType = "text/event-stream";

        /// <summary>
        /// 在 <paramref name="request"/> 上读取一段 SSE 流：每解析出一个完整事件就调用
        /// <paramref name="onEvent"/>（<b>在后台线程触发，禁止阻塞</b>，调用方自行 marshal）。
        /// 连接结束（服务端关闭流 / 网络错误 / 取消）后返回最终 <see cref="IHttpResponse"/>。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 不修改 <paramref name="request"/>：内部复制一份并接管其响应流式回调；缺省补
        /// <c>Accept: text/event-stream</c>（用户已提供 Accept 则不覆盖）。
        /// </para>
        /// <para>
        /// 与 <see cref="IHttpClient.SendAsync"/> 一致：HTTP 4xx/5xx 不抛，经
        /// <see cref="IHttpResponse.StatusCode"/> 判断；网络/TLS/超时抛 <c>CurlHttpException</c>，
        /// 取消抛 <see cref="OperationCanceledException"/>。SSE 长连接建议在 request 上设
        /// <see cref="IHttpRequest.TimeoutMs"/>=0、<see cref="IHttpRequest.TcpNoDelay"/>/
        /// <see cref="IHttpRequest.TcpKeepAlive"/>=true。
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="request"/> 已设置 <see cref="IHttpRequest.OnDataReceived"/>
        /// （SSE 需接管该回调）。
        /// </exception>
        public static Task<IHttpResponse> ReadServerSentEventsAsync(
            this IHttpClient client, IHttpRequest request,
            Action<SseEvent> onEvent, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (onEvent == null) throw new ArgumentNullException(nameof(onEvent));

            var parser = new SseEventParser();
            return RunOneConnectionAsync(client, request, parser, onEvent, onByteReceived: null, ct);
        }

        /// <summary>
        /// 单连接读取的内部实现。<paramref name="parser"/> 由调用方持有（便于上层跨重连复用其
        /// <see cref="SseEventParser.LastEventId"/>/<see cref="SseEventParser.RetryMilliseconds"/>），
        /// <paramref name="onByteReceived"/> 在每块字节到达时回调（上层做空闲/心跳计时，本层可传 null）。
        /// </summary>
        internal static Task<IHttpResponse> RunOneConnectionAsync(
            IHttpClient client, IHttpRequest request, SseEventParser parser,
            Action<SseEvent> onEvent, Action onByteReceived, CancellationToken ct)
        {
            if (request.OnDataReceived != null)
                throw new InvalidOperationException(
                    "SSE 需接管响应流式回调；请勿在传入的 request 上设置 OnDataReceived。");

            var sseRequest = CloneForSse(request);
            sseRequest.OnDataReceived = (buf, offset, len) =>
            {
                onByteReceived?.Invoke();
                parser.Feed(buf, offset, len, onEvent);
            };
            return client.SendAsync(sseRequest, ct);
        }

        /// <summary>复制为新的 <see cref="HttpRequest"/>，缺省补 Accept，不改动调用方对象。</summary>
        private static HttpRequest CloneForSse(IHttpRequest src)
        {
            return new HttpRequest
            {
                Method = src.Method,
                Url = src.Url,
                Headers = BuildHeaders(src.Headers),
                Body = src.Body,
                BodyStream = src.BodyStream,
                BodyLength = src.BodyLength,
                ConnectTimeoutMs = src.ConnectTimeoutMs,
                TimeoutMs = src.TimeoutMs,
                EnableResponseHeaders = src.EnableResponseHeaders,
                EnableCookies = src.EnableCookies,
                AutoDecompressResponse = src.AutoDecompressResponse,
                TcpNoDelay = src.TcpNoDelay,
                TcpKeepAlive = src.TcpKeepAlive,
                // OnDataReceived 故意不复制：由 SSE 接管（已在 RunOneConnectionAsync 校验未设置）
            };
        }

        private static List<KeyValuePair<string, string>> BuildHeaders(
            IEnumerable<KeyValuePair<string, string>> userHeaders)
        {
            var list = new List<KeyValuePair<string, string>>();
            bool hasAccept = false;
            if (userHeaders != null)
            {
                foreach (var kv in userHeaders)
                {
                    list.Add(kv);
                    if (string.Equals(kv.Key, AcceptHeaderName, StringComparison.OrdinalIgnoreCase))
                        hasAccept = true;
                }
            }
            if (!hasAccept)
                list.Add(new KeyValuePair<string, string>(AcceptHeaderName, EventStreamContentType));
            return list;
        }
    }
}
