using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.Sse;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    /// <summary>
    /// Layer 1 <see cref="SseCoreExtensions.ReadServerSentEventsAsync"/> 单测：用 stub
    /// <see cref="IHttpClient"/> 捕获发出的 request 并驱动其 OnDataReceived，验证 clone /
    /// Accept 注入 / 回调接管 / 事件接线，不依赖真实网络（真实流见集成测试）。
    /// </summary>
    public class SseCoreExtensionsTests
    {
        /// <summary>捕获 SendAsync 收到的 request，可选地在发送时回灌数据。</summary>
        private sealed class CapturingHttpClient : IHttpClient
        {
            public IHttpRequest Captured;
            public Func<IHttpRequest, IHttpResponse> Responder;
            public Action<IHttpRequest> OnSend;

            public Task<IHttpResponse> SendAsync(IHttpRequest request, CancellationToken ct = default)
            {
                Captured = request;
                var response = Responder?.Invoke(request) ?? new StubResponse(200);
                request.OnHeadersReceived?.Invoke(response);
                OnSend?.Invoke(request);
                return Task.FromResult(response);
            }

            public void SetProxy(HttpProxy proxy) { }
            public void ClearProxy() { }
            public void Dispose() { }
        }

        private sealed class StubResponse : IHttpResponse
        {
            public StubResponse(int status) => StatusCode = status;
            public bool IsDisposed { get; private set; }
            public int StatusCode { get; }
            public HttpVersion Version => default;
            public byte[] Body => null;
            public string ContentType => "text/event-stream";
            public long ContentLength => -1;
            public string EffectiveUrl => "";
            public int RedirectCount => 0;
            public IReadOnlyDictionary<string, string[]> Headers => null;
            public void Dispose() => IsDisposed = true;
        }

        private static void Feed(IHttpRequest r, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            r.OnDataReceived(b, 0, b.Length);
        }

        [Fact]
        public async Task ThrowsIfRequestAlreadyHasOnDataReceived()
        {
            using var client = new CapturingHttpClient();
            var req = new HttpRequest { Url = "http://x", OnDataReceived = (_, _, _) => { } };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ReadServerSentEventsAsync(req, _ => { }));
        }

        [Fact]
        public async Task InjectsAcceptHeader_WhenMissing()
        {
            using var client = new CapturingHttpClient();
            var req = new HttpRequest { Url = "http://x" };
            await client.ReadServerSentEventsAsync(req, _ => { });
            Assert.Contains(client.Captured.Headers,
                kv => kv.Key == "Accept" && kv.Value == "text/event-stream");
        }

        [Fact]
        public async Task DoesNotOverrideUserAcceptHeader()
        {
            using var client = new CapturingHttpClient();
            var req = new HttpRequest
            {
                Url = "http://x",
                Headers = new[] { new KeyValuePair<string, string>("Accept", "application/json") }
            };
            await client.ReadServerSentEventsAsync(req, _ => { });
            Assert.Single(client.Captured.Headers, kv => kv.Key == "Accept");
            Assert.Contains(client.Captured.Headers, kv => kv.Value == "application/json");
        }

        [Fact]
        public async Task DoesNotMutateUserRequest()
        {
            using var client = new CapturingHttpClient();
            var req = new HttpRequest { Url = "http://x" };
            await client.ReadServerSentEventsAsync(req, _ => { });
            Assert.Null(req.OnDataReceived);          // 用户对象未被设回调
            Assert.Null(req.OnHeadersReceived);       // 用户对象未被设回调
            Assert.Null(req.Headers);                 // 用户对象 Headers 未被改
            Assert.NotSame(req, client.Captured);     // 发出的是 clone
        }

        [Fact]
        public async Task ParsesEventsFromStreamedBytes()
        {
            using var client = new CapturingHttpClient();
            var events = new List<SseEvent>();
            client.OnSend = sent =>
            {
                Feed(sent, "data: a\n\n");
                Feed(sent, "event: ping\nda"); // 跨 chunk
                Feed(sent, "ta: b\n\n");
            };
            var req = new HttpRequest { Url = "http://x" };
            await client.ReadServerSentEventsAsync(req, events.Add);

            Assert.Equal(2, events.Count);
            Assert.Equal("a", events[0].Data);
            Assert.Equal("ping", events[1].EventType);
            Assert.Equal("b", events[1].Data);
        }

        [Fact]
        public async Task ReturnsResponseFromSend()
        {
            using var client = new CapturingHttpClient { Responder = _ => new StubResponse(204) };
            var req = new HttpRequest { Url = "http://x" };
            using var resp = await client.ReadServerSentEventsAsync(req, _ => { });
            Assert.Equal(204, resp.StatusCode);
        }

        [Fact]
        public async Task ThrowsIfRequestAlreadyHasOnHeadersReceived()
        {
            using var client = new CapturingHttpClient();
            var req = new HttpRequest { Url = "http://x", OnHeadersReceived = _ => { } };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ReadServerSentEventsAsync(req, _ => { }));
        }

        [Fact]
        public async Task SseSetsOwnOnHeadersReceived()
        {
            using var client = new CapturingHttpClient();
            var req = new HttpRequest { Url = "http://x" };
            await client.ReadServerSentEventsAsync(req, _ => { });
            Assert.NotNull(client.Captured.OnHeadersReceived);
        }

        // —— RunOneConnectionAsync 的 lastEventId 注入（供 Layer 2 重连续传用）——

        [Fact]
        public async Task RunOneConnection_InjectsLastEventId_WhenProvided()
        {
            using var client = new CapturingHttpClient();
            var parser = new SseEventParser();
            var req = new HttpRequest { Url = "http://x" };
            await SseCoreExtensions.RunOneConnectionAsync(
                client, req, parser, _ => { }, onByteReceived: null,
                CancellationToken.None, lastEventId: "5");
            Assert.Contains(client.Captured.Headers,
                kv => kv.Key == "Last-Event-ID" && kv.Value == "5");
        }

        [Fact]
        public async Task RunOneConnection_NoLastEventId_WhenNullOrEmpty()
        {
            using var client = new CapturingHttpClient();
            var parser = new SseEventParser();
            var req = new HttpRequest { Url = "http://x" };

            await SseCoreExtensions.RunOneConnectionAsync(
                client, req, parser, _ => { }, onByteReceived: null,
                CancellationToken.None, lastEventId: null);
            Assert.DoesNotContain(client.Captured.Headers, kv => kv.Key == "Last-Event-ID");

            await SseCoreExtensions.RunOneConnectionAsync(
                client, req, parser, _ => { }, onByteReceived: null,
                CancellationToken.None, lastEventId: "");
            Assert.DoesNotContain(client.Captured.Headers, kv => kv.Key == "Last-Event-ID");
        }

        [Fact]
        public async Task RunOneConnection_DoesNotOverrideUserLastEventId()
        {
            using var client = new CapturingHttpClient();
            var parser = new SseEventParser();
            var req = new HttpRequest
            {
                Url = "http://x",
                Headers = new[] { new KeyValuePair<string, string>("Last-Event-ID", "user") }
            };
            await SseCoreExtensions.RunOneConnectionAsync(
                client, req, parser, _ => { }, onByteReceived: null,
                CancellationToken.None, lastEventId: "5");
            Assert.Single(client.Captured.Headers, kv => kv.Key == "Last-Event-ID");
            Assert.Contains(client.Captured.Headers, kv => kv.Value == "user");
        }

        [Fact]
        public async Task ReadServerSentEvents_EnablesTcpKeepAlive()
        {
            // SSE 连接默认开 TCP keep-alive（底层，经 HttpRequest 内部字段，不对外暴露）
            using var client = new CapturingHttpClient();
            var req = new HttpRequest { Url = "http://x" };
            await client.ReadServerSentEventsAsync(req, _ => { });
            var sent = Assert.IsType<HttpRequest>(client.Captured);
            Assert.True(sent.TcpKeepAlive);
        }
    }
}
