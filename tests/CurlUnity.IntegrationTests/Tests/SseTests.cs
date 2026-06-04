using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using CurlUnity.Sse;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    /// <summary>
    /// Layer 1 <see cref="SseCoreExtensions.ReadServerSentEventsAsync"/> 集成测试：
    /// 通过 Kestrel SSE 端点验证真实网络下的事件流、连接结束、后台线程契约、POST body、取消。
    /// </summary>
    [Collection("Integration")]
    public class SseTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public SseTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        /// <summary>给长连接读取套一个上限，避免断言失败时测试挂死。</summary>
        private static async Task<T> WithTimeout<T>(Task<T> task, int ms = 15000)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)) != task)
                throw new TimeoutException("SSE test timed out");
            return await task;
        }

        [Fact]
        public async Task ReceivesAllEvents_AndConnectionEnds()
        {
            var events = new List<SseEvent>();
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/sse?count=3" };

            using var resp = await WithTimeout(
                _client.ReadServerSentEventsAsync(req, e => { lock (events) events.Add(e); }));

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(3, events.Count);
            Assert.All(events, e => Assert.Equal("tick", e.EventType));
            Assert.Equal(new[] { "msg-0", "msg-1", "msg-2" }, events.Select(e => e.Data));
            Assert.Equal(new[] { "0", "1", "2" }, events.Select(e => e.LastEventId));
        }

        [Fact]
        public async Task Callback_RunsOnBackgroundThread()
        {
            int testThreadId = Thread.CurrentThread.ManagedThreadId;
            int? callbackThreadId = null;

            var req = new HttpRequest { Url = $"{_server.HttpUrl}/sse?count=1" };
            await WithTimeout(_client.ReadServerSentEventsAsync(
                req, e => callbackThreadId ??= Thread.CurrentThread.ManagedThreadId));

            Assert.NotNull(callbackThreadId);
            Assert.NotEqual(testThreadId, callbackThreadId);
        }

        [Fact]
        public async Task PostBody_EchoedAsEvent()
        {
            var events = new List<SseEvent>();
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/sse-post",
                Body = Encoding.UTF8.GetBytes("hello-sse"),
            };

            using var resp = await WithTimeout(_client.ReadServerSentEventsAsync(req, events.Add));

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal("hello-sse", Assert.Single(events).Data);
        }

        [Fact]
        public async Task Cancellation_StopsReading()
        {
            using var cts = new CancellationTokenSource();
            var firstEvent = new TaskCompletionSource();

            // count 大 + 事件间隔，给取消留出时间窗口
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/sse?count=1000&delayMs=50",
                TimeoutMs = 0,
            };
            var task = _client.ReadServerSentEventsAsync(req, _ => firstEvent.TrySetResult(), cts.Token);

            await WithTimeout(firstEvent.Task.ContinueWith(_ => true)); // 等到第一个事件
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithTimeout(task));
        }
    }
}
