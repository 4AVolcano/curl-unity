using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// Layer 2 <see cref="SseConnection"/> 重连循环单测：用可控 stub <see cref="IHttpClient"/>
    /// 确定性驱动每轮连接（喂字节 / EOF / 抛异常 / 阻塞至取消），不依赖真实网络与计时。
    /// 所有用例 <c>ReconnectDelayInit=Zero</c> 去除时序抖动；需要观察事件的用例用 gate
    /// 保证「挂回调 → 释放 → 产数据」的顺序（规避 construct-then-subscribe 竞态）。
    /// </summary>
    public class SseConnectionTests
    {
        private enum Kind { Eof, Throw, Block }

        private sealed class Behavior
        {
            public Kind Kind;
            public int StatusCode = 200;
            public string Bytes;
            public Exception Exception;

            public static Behavior Eof(int status = 200, string bytes = null)
                => new Behavior { Kind = Kind.Eof, StatusCode = status, Bytes = bytes };
            public static Behavior Throw(Exception ex, string bytes = null)
                => new Behavior { Kind = Kind.Throw, Exception = ex, Bytes = bytes };
            public static Behavior Block(string bytes = null)
                => new Behavior { Kind = Kind.Block, Bytes = bytes };
        }

        private sealed class ControllableHttpClient : IHttpClient
        {
            private readonly Func<int, Behavior> _plan;
            private int _calls;
            public readonly List<IHttpRequest> Requests = new();
            public Task Gate = Task.CompletedTask; // 默认不拦；需要确定性的用例设为未完成的 TCS
            public int CallCount => Volatile.Read(ref _calls);

            public ControllableHttpClient(Func<int, Behavior> plan) => _plan = plan;

            public async Task<IHttpResponse> SendAsync(IHttpRequest request, CancellationToken ct)
            {
                await Gate.ConfigureAwait(false); // 在测试挂好回调并 release 前不产数据
                int idx = Interlocked.Increment(ref _calls) - 1;
                lock (Requests) Requests.Add(request);
                var b = _plan(idx);

                var response = new StubResponse(b.StatusCode);
                request.OnHeadersReceived?.Invoke(response);

                if (b.Bytes != null && request.OnDataReceived != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(b.Bytes);
                    request.OnDataReceived(bytes, 0, bytes.Length);
                }

                switch (b.Kind)
                {
                    case Kind.Throw:
                        throw b.Exception;
                    case Kind.Block:
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (ct.Register(() => tcs.TrySetResult(true))) await tcs.Task;
                        throw new OperationCanceledException(ct);
                    default:
                        return response;
                }
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

        private static SseConnectionOptions ZeroDelay() => new SseConnectionOptions
        {
            ReconnectDelayInit = TimeSpan.Zero,
            ReconnectDelayIncFn = _ => TimeSpan.Zero,
        };

        private static Func<CancellationToken, Task<IHttpRequest>> Factory(string url = "http://x")
            => _ => Task.FromResult<IHttpRequest>(new HttpRequest { Url = url });

        /// <summary>创建一个被 gate 拦住的 stub + release 委托（挂好回调后调 release 再产数据）。</summary>
        private static (ControllableHttpClient client, Action release) Gated(Func<int, Behavior> plan)
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new ControllableHttpClient(plan) { Gate = gate.Task };
            return (client, () => gate.TrySetResult(true));
        }

        private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException("condition not met in time");
                await Task.Delay(5);
            }
        }

        // ---- 用例 ----

        [Fact]
        public async Task ReconnectsAfterEof()
        {
            var client = new ControllableHttpClient(idx => idx == 0 ? Behavior.Eof() : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 2); // EOF 后自动重连
            Assert.True(client.CallCount >= 2);
        }

        [Fact]
        public async Task StateSequence_ConnectingOpenReconnectingOpen()
        {
            var states = new List<SseConnectionState>();
            int Count(SseConnectionState s) { lock (states) return states.Count(x => x == s); }

            var (client, release) = Gated(idx =>
                idx == 0 ? Behavior.Eof(bytes: "data: a\n\n") : Behavior.Block(bytes: "data: b\n\n"));
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);
            Assert.Equal(SseConnectionState.Connecting, conn.State); // 初始值（gate 未放，无任何转换）
            conn.OnStateChanged += (_, n) => { lock (states) states.Add(n); };
            release();

            await WaitUntil(() => Count(SseConnectionState.Open) >= 2);
            conn.Dispose();
            await WaitUntil(() => conn.State == SseConnectionState.Closed);

            List<SseConnectionState> snap;
            lock (states) snap = states.ToList();
            // 转换序列：Open → Reconnecting → Open → … → Closed（初始 Connecting 不经 OnStateChanged）
            Assert.Equal(SseConnectionState.Open, snap[0]);
            Assert.Equal(SseConnectionState.Reconnecting, snap[1]);
            Assert.Equal(SseConnectionState.Open, snap[2]);
            Assert.Equal(SseConnectionState.Closed, snap[^1]);
        }

        [Fact]
        public async Task InjectsLastEventId_OnReconnect()
        {
            var client = new ControllableHttpClient(idx =>
                idx == 0 ? Behavior.Eof(bytes: "id: 5\ndata: x\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 2);
            IHttpRequest second;
            lock (client.Requests) second = client.Requests[1];
            Assert.Contains(second.Headers, kv => kv.Key == "Last-Event-ID" && kv.Value == "5");
        }

        [Fact]
        public async Task NonSuccessStatus_RaisesOnError_AndReconnects()
        {
            var errors = new List<Exception>();
            var (client, release) = Gated(idx => idx == 0 ? Behavior.Eof(503) : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);
            conn.OnError += e => { lock (errors) errors.Add(e); };
            release();

            await WaitUntil(() =>
            {
                lock (errors) return errors.Count >= 1 && client.CallCount >= 2;
            });
            Exception first;
            lock (errors) first = errors[0];
            var status = Assert.IsType<SseHttpStatusException>(first);
            Assert.Equal(503, status.StatusCode);
        }

        [Fact]
        public async Task Exception_RaisesOnError_AndReconnects()
        {
            var errors = new List<Exception>();
            var boom = new IOException("boom");
            var (client, release) = Gated(idx => idx == 0 ? Behavior.Throw(boom) : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);
            conn.OnError += e => { lock (errors) errors.Add(e); };
            release();

            await WaitUntil(() =>
            {
                lock (errors) return errors.Count >= 1 && client.CallCount >= 2;
            });
            Exception first;
            lock (errors) first = errors[0];
            Assert.Same(boom, first);
        }

        [Fact]
        public async Task Dispose_StopsLoop_AndClosesState()
        {
            var client = new ControllableHttpClient(_ => Behavior.Block());
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 1);
            conn.Dispose();
            await WaitUntil(() => conn.State == SseConnectionState.Closed);

            int after = client.CallCount;
            await Task.Delay(50);
            Assert.Equal(after, client.CallCount); // Dispose 后不再发起连接
        }

        [Fact]
        public async Task Backoff_IncFnInvoked_BetweenReconnects()
        {
            int incCalls = 0;
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = _ => { Interlocked.Increment(ref incCalls); return TimeSpan.Zero; },
            };
            // 连续无 data 的 EOF（不触发 Open，不重置退避）→ 每次重连都调 IncFn
            var client = new ControllableHttpClient(idx => idx < 2 ? Behavior.Eof() : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 3);
            Assert.True(Volatile.Read(ref incCalls) >= 2);
        }

        [Fact]
        public void DefaultBackoff_DoublesAndClamps()
        {
            var f = new SseConnectionOptions().ReconnectDelayIncFn;
            Assert.Equal(TimeSpan.FromSeconds(2), f(TimeSpan.FromSeconds(1)));
            Assert.Equal(TimeSpan.FromSeconds(8), f(TimeSpan.FromSeconds(4)));
            Assert.Equal(TimeSpan.FromSeconds(32), f(TimeSpan.FromSeconds(20))); // 40 → clamp 32
            Assert.Equal(TimeSpan.FromSeconds(1), f(TimeSpan.Zero));             // 0 → floor 1
        }

        // ---- OpenSse 入口守卫 ----

        [Fact]
        public void OpenSse_NullRequest_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            Assert.Throws<ArgumentNullException>(() => client.OpenSse((IHttpRequest)null));
        }

        [Fact]
        public void OpenSse_RequestWithOnDataReceived_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var req = new HttpRequest { Url = "http://x", OnDataReceived = (_, _, _) => { } };
            Assert.Throws<InvalidOperationException>(() => client.OpenSse(req));
        }

        [Fact]
        public void OpenSse_NullFactory_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            Assert.Throws<ArgumentNullException>(
                () => client.OpenSse((Func<CancellationToken, Task<IHttpRequest>>)null));
        }

        [Fact]
        public async Task OpenSse_StaticRequest_Connects()
        {
            var client = new ControllableHttpClient(_ => Behavior.Block());
            using var conn = client.OpenSse(new HttpRequest { Url = "http://x" }, ZeroDelay());
            await WaitUntil(() => client.CallCount >= 1);
            Assert.Equal(SseConnectionState.Connecting, conn.State); // 阻塞中、未收到字节 → 仍 Connecting
        }

        // ---- Codex Layer 2 评审修复 ----

        [Fact]
        public async Task Status204_StopsReconnecting()
        {
            var errors = new List<Exception>();
            var (client, release) = Gated(_ => Behavior.Eof(204)); // 始终 204
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);
            conn.OnError += e => { lock (errors) errors.Add(e); };
            release();

            await WaitUntil(() => conn.State == SseConnectionState.Closed);
            await Task.Delay(50);
            Assert.Equal(1, client.CallCount);  // 204 = 服务端要求停止 → 不重连
            lock (errors) Assert.Empty(errors); // 204 不报错
        }

        [Fact]
        public async Task Dispose_ClosedCallback_NotFiredSynchronously()
        {
            bool closedDuringDispose = false;
            int inDispose = 0;
            var (client, release) = Gated(_ => Behavior.Block());
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);
            conn.OnStateChanged += (_, n) =>
            {
                if (n == SseConnectionState.Closed && Volatile.Read(ref inDispose) == 1) closedDuringDispose = true;
            };
            release();
            await WaitUntil(() => client.CallCount >= 1);

            Volatile.Write(ref inDispose, 1);
            conn.Dispose();
            Volatile.Write(ref inDispose, 0);

            await WaitUntil(() => conn.State == SseConnectionState.Closed);
            Assert.False(closedDuringDispose); // Closed 回调不应在 Dispose 调用线程同步触发（契约：后台线程）
        }

        [Fact]
        public void Options_NegativeReconnectDelayInit_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var opts = new SseConnectionOptions { ReconnectDelayInit = TimeSpan.FromMilliseconds(-5) };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SseConnection(client, Factory(), opts, CancellationToken.None));
        }

        [Fact]
        public void Options_NullIncFn_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var opts = new SseConnectionOptions { ReconnectDelayIncFn = null };
            Assert.Throws<ArgumentNullException>(
                () => new SseConnection(client, Factory(), opts, CancellationToken.None));
        }

        [Fact]
        public void Options_NonPositiveIdleTimeout_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var opts = new SseConnectionOptions { IdleTimeout = TimeSpan.Zero };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SseConnection(client, Factory(), opts, CancellationToken.None));
        }

        [Fact]
        public async Task Backoff_NotReset_OnNonSuccessWithBody()
        {
            var delays = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => { lock (delays) delays.Add(d); return d + TimeSpan.FromMilliseconds(1); },
            };
            // 503 带 SSE 格式 body：退避不应因 body 字节被重置（旧实现会在 onByteReceived 里重置）
            var client = new ControllableHttpClient(idx => idx < 3 ? Behavior.Eof(503, bytes: "data: x\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 4);
            lock (delays) Assert.Contains(delays, d => d >= TimeSpan.FromMilliseconds(2)); // 退避在累积，未被重置
        }
    }
}
