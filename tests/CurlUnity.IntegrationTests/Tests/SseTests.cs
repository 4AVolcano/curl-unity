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

        private static async Task WithTimeout(Task task, int ms = 15000)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)) != task)
                throw new TimeoutException("SSE test timed out");
            await task;
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

        // ============ Layer 2：OpenSse / ISseConnection（重连便利层） ============

        private static async Task WaitUntil(Func<bool> cond, int ms = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met in time");
                await Task.Delay(20);
            }
        }

        [Fact]
        public async Task OpenSse_ReconnectsAndContinuesViaLastEventId()
        {
            var events = new List<SseEvent>();
            var options = new SseConnectionOptions { ReconnectDelayInit = TimeSpan.FromMilliseconds(20) };
            // count=5 全局事件，dropAfter=2 每连接发 2 条就断 → 需 3 次连接，靠 Last-Event-ID 续传
            using var sse = _client.OpenSse(
                new HttpRequest { Url = $"{_server.HttpUrl}/sse?count=5&dropAfter=2", TimeoutMs = 0 },
                options);
            sse.OnEvent += e => { lock (events) events.Add(e); };

            await WaitUntil(() => { lock (events) return events.Count >= 5; });

            List<SseEvent> snap;
            lock (events) snap = events.Take(5).ToList();
            Assert.Equal(new[] { "msg-0", "msg-1", "msg-2", "msg-3", "msg-4" }, snap.Select(e => e.Data).ToArray());
            Assert.Equal(new[] { "0", "1", "2", "3", "4" }, snap.Select(e => e.LastEventId).ToArray()); // 续传不重复、id 续上
        }

        [Fact]
        public async Task OpenSse_IdleTimeout_Reconnects()
        {
            var errors = new List<Exception>();
            var states = new List<SseConnectionState>();
            var options = new SseConnectionOptions
            {
                IdleTimeout = TimeSpan.FromMilliseconds(200),
                ReconnectDelayInit = TimeSpan.FromMilliseconds(20),
            };
            using var sse = _client.OpenSse(
                new HttpRequest { Url = $"{_server.HttpUrl}/sse-idle?silentMs=3000", TimeoutMs = 0 },
                options);
            sse.OnError += e => { lock (errors) errors.Add(e); };
            sse.OnStateChanged += (_, n) => { lock (states) states.Add(n); };

            await WaitUntil(() => { lock (errors) return errors.OfType<TimeoutException>().Any(); });
            lock (states) Assert.Contains(states, s => s == SseConnectionState.Reconnecting);
        }

        [Fact]
        public async Task OpenSse_NonSuccess_RaisesError_AndFactoryReinvoked()
        {
            var errors = new List<Exception>();
            int calls = 0;
            var options = new SseConnectionOptions { ReconnectDelayInit = TimeSpan.FromMilliseconds(20) };
            // 用 requestFactory 重载：顺带验证 async headers 钩子每轮(重)连被调用
            using var sse = _client.OpenSse(
                _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult<IHttpRequest>(
                        new HttpRequest { Url = $"{_server.HttpUrl}/sse-503", TimeoutMs = 0 });
                },
                options);
            sse.OnError += e => { lock (errors) errors.Add(e); };

            await WaitUntil(() => { lock (errors) return errors.OfType<SseHttpStatusException>().Any(); });
            SseHttpStatusException status;
            lock (errors) status = errors.OfType<SseHttpStatusException>().First();
            Assert.Equal(503, status.StatusCode);
            await WaitUntil(() => Volatile.Read(ref calls) >= 2); // 重连 → requestFactory 再次被调
        }

        [Fact]
        public async Task OpenSse_Dispose_ClosesCleanly()
        {
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sse = _client.OpenSse(
                new HttpRequest { Url = $"{_server.HttpUrl}/sse?count=1000&delayMs=30", TimeoutMs = 0 },
                new SseConnectionOptions());
            sse.OnEvent += _ => first.TrySetResult(true);

            await WithTimeout(first.Task);
            sse.Dispose();
            await WaitUntil(() => sse.State == SseConnectionState.Closed, 3000);
            Assert.Equal(SseConnectionState.Closed, sse.State);
        }

        [Fact]
        public async Task OpenSse_Callback_OnBackgroundThread()
        {
            int testTid = Thread.CurrentThread.ManagedThreadId;
            int cbTid = 0;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sse = _client.OpenSse(
                new HttpRequest { Url = $"{_server.HttpUrl}/sse?count=1", TimeoutMs = 0 },
                new SseConnectionOptions());
            sse.OnEvent += _ =>
            {
                Interlocked.CompareExchange(ref cbTid, Thread.CurrentThread.ManagedThreadId, 0);
                done.TrySetResult(true);
            };

            await WithTimeout(done.Task);
            Assert.NotEqual(0, cbTid);
            Assert.NotEqual(testTid, cbTid);
        }

        [Fact]
        public async Task OpenSse_MaxReconnectAttempts_FaultsCompletion()
        {
            // 真实网络下持续 503（不会建立）→ 达到次数上限放弃 → Completion 以 SseReconnectExhaustedException fault
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.FromMilliseconds(10),
                MaxReconnectAttempts = 2,
            };
            using var sse = _client.OpenSse(
                new HttpRequest { Url = $"{_server.HttpUrl}/sse-503", TimeoutMs = 0 },
                options);

            var ex = await Assert.ThrowsAsync<SseReconnectExhaustedException>(() => WithTimeout(sse.Completion));
            Assert.Equal(2, ex.AttemptCount);
            Assert.Equal(SseConnectionState.Closed, sse.State);
        }

        [Fact]
        public async Task OpenSse_Completion_GracefulOnDispose()
        {
            // 用带回调参数的 race-free 重载在构造期挂回调；Dispose 后 Completion 优雅完成（不 fault）。
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sse = _client.OpenSse(
                new HttpRequest { Url = $"{_server.HttpUrl}/sse?count=1000&delayMs=30", TimeoutMs = 0 },
                onEvent: _ => first.TrySetResult(true));

            await WithTimeout(first.Task);
            sse.Dispose();
            await WithTimeout(sse.Completion);
            Assert.True(sse.Completion.IsCompletedSuccessfully);
        }
    }
}
