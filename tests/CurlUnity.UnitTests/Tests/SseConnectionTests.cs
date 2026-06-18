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
        public void OpenSse_RequestWithOnHeadersReceived_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var req = new HttpRequest { Url = "http://x", OnHeadersReceived = _ => { } };
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
        public async Task Backoff_NotReset_OnRepeatedEmptyEof()
        {
            // 连续空 2xx EOF（无字节）不应被视为"连接成功"：退避必须持续递增，
            // 否则会以 init 间隔无限热循环猛打服务端（旧实现在每个 2xx EOF 后无条件重置退避）。
            var seen = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => { lock (seen) seen.Add(d); return d + TimeSpan.FromMilliseconds(1); },
            };
            var client = new ControllableHttpClient(idx => idx < 4 ? Behavior.Eof() : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 5);
            lock (seen) Assert.Contains(seen, d => d >= TimeSpan.FromMilliseconds(2)); // 退避在增长（未被重置）
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

        // ---- 退避重置语义：建立 vs 短连接阈值 ----

        [Fact]
        public async Task Backoff_Reset_OnEstablishedConnection_ByDefault()
        {
            // 默认 BackoffResetThreshold=0：收到字节即视为已建立 → 每轮断开都重置退避，
            // 因而从不进入退避递增（IncFn 永不被调用）。
            var grew = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => { lock (grew) grew.Add(d); return d + TimeSpan.FromMilliseconds(5); },
            };
            var client = new ControllableHttpClient(idx => idx < 3 ? Behavior.Eof(bytes: "data: x\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 3);
            lock (grew) Assert.Empty(grew); // 已建立 → 退避重置、不递增
        }

        [Fact]
        public async Task BackoffResetThreshold_NoResetWhenUptimeBelowThreshold()
        {
            // 阈值设为 1h：连接虽收到字节但瞬间断开（uptime≈0 << 1h），不算"已建立" → 退避持续递增，
            // 防御"建连即断"的抖动服务端热循环。
            var grew = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => { lock (grew) grew.Add(d); return d + TimeSpan.FromMilliseconds(1); },
                BackoffResetThreshold = TimeSpan.FromHours(1),
            };
            var client = new ControllableHttpClient(idx => idx < 4 ? Behavior.Eof(bytes: "data: x\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 5);
            lock (grew) Assert.Contains(grew, d => d >= TimeSpan.FromMilliseconds(2)); // 递增，未因短连接重置
        }

        [Fact]
        public async Task Backoff_Reset_OnEstablished_EvenWhenConnectionEndsWithError()
        {
            // 「是否真正建立」只看本轮是否收到字节 + 存活是否达 BackoffResetThreshold，与连接「如何结束」
            // 无关（干净 EOF / 网络错误 / 空闲超时 一视同仁）。生产中连接绝大多数以网络错误/超时断开
            // 而非干净 EOF——若仅干净 EOF 才算建立，退避会几乎永远递增到上限，形同失效。
            var grew = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => { lock (grew) grew.Add(d); return d + TimeSpan.FromMilliseconds(5); },
            };
            // 每轮：先交付字节(建立) 再抛网络错误 → established=true → 退避应重置、IncFn 不应被调用
            var client = new ControllableHttpClient(idx =>
                idx < 3 ? Behavior.Throw(new IOException("drop"), bytes: "data: x\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 3);
            lock (grew) Assert.Empty(grew); // 已建立(即便以错误结束) → 退避重置、不递增
        }

        [Fact]
        public async Task MaxReconnectAttempts_NotExhausted_WhenEachConnectionEstablishes()
        {
            // MaxReconnectAttempts 计的是「连续失败」次数，建立成功即清零。每轮都收到字节(建立)后以
            // 错误断开，应永不触发次数上限——否则健康但短命的连接会被误判为放弃（生产级 bug）。
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => d,
                MaxReconnectAttempts = 2,
                DelayProvider = (_, __) => Task.CompletedTask, // 不真实等待
            };
            var client = new ControllableHttpClient(idx =>
                idx < 5 ? Behavior.Throw(new IOException("drop"), bytes: "data: x\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 5); // 远超 MaxReconnectAttempts=2 仍在重连 → 未放弃
            await Task.Delay(30);
            Assert.False(conn.Completion.IsCompleted); // 未因次数上限 fault / 终止，仍在运行
        }

        // ---- 重连延迟计算：retry 覆盖 / 上限 / 抖动 ----

        [Fact]
        public async Task RetryOverride_UsedAsReconnectDelay()
        {
            var seen = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.FromMilliseconds(1000),
                ReconnectDelayIncFn = d => d,
                DelayProvider = (d, _) => { lock (seen) seen.Add(d); return Task.CompletedTask; },
            };
            // 首连交付 retry:250 后 EOF → 之后重连用 250ms（覆盖退避基准）
            var client = new ControllableHttpClient(idx => idx == 0 ? Behavior.Eof(bytes: "retry: 250\n\n") : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => { lock (seen) return seen.Count >= 1; });
            lock (seen) Assert.Equal(TimeSpan.FromMilliseconds(250), seen[0]);
        }

        [Fact]
        public async Task MaxReconnectDelay_ClampsBackoff()
        {
            var seen = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.FromMilliseconds(1000),
                ReconnectDelayIncFn = d => d + d, // 1000 → 2000 → 4000...
                MaxReconnectDelay = TimeSpan.FromMilliseconds(1500),
                DelayProvider = (d, _) => { lock (seen) seen.Add(d); return Task.CompletedTask; },
            };
            var client = new ControllableHttpClient(idx => idx < 4 ? Behavior.Eof() : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => { lock (seen) return seen.Count >= 3; });
            lock (seen)
            {
                Assert.All(seen, d => Assert.True(d <= TimeSpan.FromMilliseconds(1500), $"{d} 超过上限"));
                Assert.Contains(seen, d => d == TimeSpan.FromMilliseconds(1500));
            }
        }

        [Fact]
        public async Task Jitter_ScalesComputedDelay_Deterministically()
        {
            var seen = new List<TimeSpan>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.FromMilliseconds(100),
                ReconnectDelayIncFn = d => d,
                ReconnectJitter = 0.5,
                JitterRng = () => 0.5, // factor = 1 - 0.5*0.5 = 0.75
                DelayProvider = (d, _) => { lock (seen) seen.Add(d); return Task.CompletedTask; },
            };
            var client = new ControllableHttpClient(idx => idx < 2 ? Behavior.Eof() : Behavior.Block());
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => { lock (seen) return seen.Count >= 1; });
            lock (seen) Assert.Equal(TimeSpan.FromMilliseconds(75), seen[0]); // 100ms * 0.75
        }

        // ---- 放弃策略：次数上限 / ShouldReconnect ----

        [Fact]
        public async Task Completion_FaultsOnMaxReconnectAttempts()
        {
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ReconnectDelayIncFn = d => d,
                MaxReconnectAttempts = 3,
                DelayProvider = (_, __) => Task.CompletedTask, // 不真实等待
            };
            var client = new ControllableHttpClient(_ => Behavior.Eof()); // 永远空 EOF（不会建立）
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<SseReconnectExhaustedException>(() => conn.Completion);
            Assert.Equal(3, ex.AttemptCount);
            Assert.Equal(3, client.CallCount); // 连续失败 3 次后放弃，不再发起第 4 次
            Assert.Equal(SseConnectionState.Closed, conn.State);
        }

        [Fact]
        public async Task ShouldReconnect_False_StopsReconnect_AndCompletesGracefully()
        {
            var errors = new List<Exception>();
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                ShouldReconnect = ex => !(ex is SseHttpStatusException), // 非 2xx 不重连
            };
            var client = new ControllableHttpClient(_ => Behavior.Eof(503));
            using var conn = new SseConnection(client, Factory(), options, CancellationToken.None,
                onError: e => { lock (errors) errors.Add(e); });

            await conn.Completion; // 优雅结束，不 fault
            Assert.True(conn.Completion.IsCompletedSuccessfully);
            Assert.Equal(1, client.CallCount); // 判定停止 → 不重连
            lock (errors) Assert.IsType<SseHttpStatusException>(Assert.Single(errors));
        }

        // ---- Completion 可观测性 / 回调异常 / 构造期挂回调 ----

        [Fact]
        public async Task Completion_CompletesGracefully_OnDispose()
        {
            var client = new ControllableHttpClient(_ => Behavior.Block());
            var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 1);
            conn.Dispose();
            await conn.Completion; // 不抛
            Assert.True(conn.Completion.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task Completion_FaultsWhenOnEventThrows()
        {
            // 用户事件回调抛异常：连接终止，异常经 Completion fault 暴露（不静默吞、不无限重连）。
            var boom = new InvalidOperationException("handler boom");
            var client = new ControllableHttpClient(_ => Behavior.Block(bytes: "data: x\n\n"));
            using var conn = new SseConnection(client, Factory(), ZeroDelay(), CancellationToken.None,
                onEvent: _ => throw boom); // 构造期挂回调（同时验证 race-free 路径）

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.Completion);
            Assert.Same(boom, ex);
            await WaitUntil(() => conn.State == SseConnectionState.Closed);
        }

        [Fact]
        public async Task OpenSse_HandlersAtConstruction_ReceiveFirstEvent()
        {
            // 通过带回调参数的 OpenSse 重载在构造期挂回调：即便 stub 立即产数据也不漏首个事件。
            var events = new List<SseEvent>();
            var client = new ControllableHttpClient(idx => idx == 0
                ? Behavior.Eof(bytes: "data: hi\n\n")
                : Behavior.Block());
            using var conn = client.OpenSse(
                new HttpRequest { Url = "http://x" },
                onEvent: e => { lock (events) events.Add(e); },
                options: ZeroDelay());

            await WaitUntil(() => { lock (events) return events.Count >= 1; });
            lock (events) Assert.Equal("hi", events[0].Data);
        }

        // ---- 空闲超时 CTS 在 Dispose 下的清理安全 ----

        [Fact]
        public async Task Dispose_WithIdleTimeout_ClosesCleanly()
        {
            var options = new SseConnectionOptions
            {
                ReconnectDelayInit = TimeSpan.Zero,
                IdleTimeout = TimeSpan.FromSeconds(30),
            };
            var client = new ControllableHttpClient(_ => Behavior.Block());
            var conn = new SseConnection(client, Factory(), options, CancellationToken.None);

            await WaitUntil(() => client.CallCount >= 1);
            conn.Dispose();
            await conn.Completion; // 空闲 CTS 清理与 Dispose 无竞态 → 干净结束
            Assert.Equal(SseConnectionState.Closed, conn.State);
        }

        // ---- 新增 options 校验 ----

        [Fact]
        public void Options_JitterOutOfRange_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var opts = new SseConnectionOptions { ReconnectJitter = 1.5 };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SseConnection(client, Factory(), opts, CancellationToken.None));
        }

        [Fact]
        public void Options_NegativeMaxAttempts_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var opts = new SseConnectionOptions { MaxReconnectAttempts = -1 };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SseConnection(client, Factory(), opts, CancellationToken.None));
        }

        [Fact]
        public void Options_NonPositiveMaxReconnectDelay_Throws()
        {
            using var client = new ControllableHttpClient(_ => Behavior.Block());
            var opts = new SseConnectionOptions { MaxReconnectDelay = TimeSpan.Zero };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SseConnection(client, Factory(), opts, CancellationToken.None));
        }
    }
}
