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
        private const string LastEventIdHeaderName = "Last-Event-ID";
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
        /// 非 2xx 状态码在 body 到达前通过 <see cref="IHttpRequest.OnHeadersReceived"/>
        /// 以 <see cref="SseHttpStatusException"/> 抛出，不会解析无效的事件流。
        /// 2xx 响应正常返回 <see cref="IHttpResponse"/>（含 204）。
        /// 网络/TLS/超时抛 <c>CurlHttpException</c>，取消抛 <see cref="OperationCanceledException"/>。
        /// SSE 长连接建议在 request 上设
        /// <see cref="IHttpRequest.TimeoutMs"/>=0；本方法内部已为该连接开启 TCP keep-alive。
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="request"/> 已设置 <see cref="IHttpRequest.OnDataReceived"/>
        /// （SSE 需接管该回调）。
        /// </exception>
        /// <exception cref="SseHttpStatusException">
        /// 服务端返回非 2xx HTTP 状态码。
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
        /// <see cref="SseEventParser.LastEventId"/>/<see cref="SseEventParser.RetryMilliseconds"/>）。
        /// <para>
        /// <b>本方法不重置 parser</b>：跨连接复用同一 parser 时，调用方须在每个连接边界先调用
        /// <see cref="SseEventParser.Reset"/>，清理上一连接的半行/半事件与 BOM 状态，否则会串流。
        /// 公开入口 <see cref="ReadServerSentEventsAsync"/> 每次新建 parser，无此顾虑。
        /// </para>
        /// <paramref name="onByteReceived"/> 在每块字节到达时回调（上层做空闲/心跳计时，本层可传 null）。
        /// </summary>
        internal static Task<IHttpResponse> RunOneConnectionAsync(
            IHttpClient client, IHttpRequest request, SseEventParser parser,
            Action<SseEvent> onEvent, Action onByteReceived, CancellationToken ct,
            string lastEventId = null)
        {
            if (request.OnDataReceived != null)
                throw new InvalidOperationException(
                    "SSE 需接管响应流式回调；请勿在传入的 request 上设置 OnDataReceived。");
            if (request.OnHeadersReceived != null)
                throw new InvalidOperationException(
                    "SSE 需接管 OnHeadersReceived（用于非 2xx 快速失败）；请勿在传入的 request 上设置 OnHeadersReceived。");

            var sseRequest = CloneForSse(request, lastEventId);
            sseRequest.OnHeadersReceived = resp =>
            {
                if (resp.StatusCode < 200 || resp.StatusCode >= 300)
                    throw new SseHttpStatusException(resp.StatusCode);
            };
            sseRequest.OnDataReceived = (buf, offset, len) =>
            {
                onByteReceived?.Invoke();
                parser.Feed(buf, offset, len, onEvent);
            };
            return client.SendAsync(sseRequest, ct);
        }

        /// <summary>复制为新的 <see cref="HttpRequest"/>，缺省补 Accept 与（可选）Last-Event-ID，不改动调用方对象。</summary>
        private static HttpRequest CloneForSse(IHttpRequest src, string lastEventId)
        {
            return new HttpRequest
            {
                Method = src.Method,
                Url = src.Url,
                Headers = BuildHeaders(src.Headers, lastEventId),
                Body = src.Body,
                BodyStream = src.BodyStream,
                BodyLength = src.BodyLength,
                ConnectTimeoutMs = src.ConnectTimeoutMs,
                TimeoutMs = src.TimeoutMs,
                EnableResponseHeaders = src.EnableResponseHeaders,
                EnableCookies = src.EnableCookies,
                AutoDecompressResponse = src.AutoDecompressResponse,
                TcpKeepAlive = true, // SSE 长连接默认开 TCP keep-alive（HttpRequest 内部字段）
                // OnDataReceived / OnHeadersReceived 故意不复制：由 SSE 层自行决定是否使用
            };
        }

        private static List<KeyValuePair<string, string>> BuildHeaders(
            IEnumerable<KeyValuePair<string, string>> userHeaders, string lastEventId)
        {
            var list = new List<KeyValuePair<string, string>>();
            bool hasAccept = false, hasLastEventId = false;
            if (userHeaders != null)
            {
                foreach (var kv in userHeaders)
                {
                    list.Add(kv);
                    if (string.Equals(kv.Key, AcceptHeaderName, StringComparison.OrdinalIgnoreCase))
                        hasAccept = true;
                    else if (string.Equals(kv.Key, LastEventIdHeaderName, StringComparison.OrdinalIgnoreCase))
                        hasLastEventId = true;
                }
            }
            if (!hasAccept)
                list.Add(new KeyValuePair<string, string>(AcceptHeaderName, EventStreamContentType));
            if (!hasLastEventId && !string.IsNullOrEmpty(lastEventId))
                list.Add(new KeyValuePair<string, string>(LastEventIdHeaderName, lastEventId));
            return list;
        }
    }
}
