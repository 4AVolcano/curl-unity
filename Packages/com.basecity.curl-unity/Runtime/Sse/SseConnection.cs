using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;

namespace CurlUnity.Sse
{
    /// <summary>
    /// <see cref="ISseConnection"/> 的实现：在 Layer 1 <see cref="SseCoreExtensions.RunOneConnectionAsync"/>
    /// 之上跑重连循环，叠加指数退避（含 jitter / 上限 / 次数与时长封顶 / 自定义判定）、<c>Last-Event-ID</c>
    /// 注入、<c>retry:</c> 响应、空闲/心跳超时、状态机。构造即开始连接（循环在后台线程运行）。
    /// </summary>
    /// <remarks>
    /// 终止可观测性：循环结束（取消/Dispose/204/放弃/回调异常/未预期异常）总会完成 <see cref="Completion"/>，
    /// 详见其文档。所有用户回调经 <see cref="RaiseEvent"/>/<see cref="RaiseError"/>/<see cref="SetState"/>
    /// 守护调用——回调抛出会被捕获、记录为终止原因并请求停止，绝不静默丢弃，也不会让后台 Task 变成未观测异常。
    /// </remarks>
    internal sealed class SseConnection : ISseConnection
    {
        private readonly IHttpClient _client;
        private readonly Func<CancellationToken, Task<IHttpRequest>> _requestFactory;
        private readonly SseConnectionOptions _options;
        private readonly SseEventParser _parser = new SseEventParser();
        private readonly Func<double> _rng;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TaskCompletionSource<bool> _completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // _disposeCts / _linkedCt 的 Cancel 与 Dispose 必须互斥（CTS 不允许并发 Cancel/Dispose）：
        // 用 _ctsGate 串行化，_ctsDisposed 标记已由循环 finally 释放，避免 Dispose 线程对已释放 CTS 再 Cancel。
        private readonly object _ctsGate = new object();
        private readonly CancellationTokenSource _disposeCts;
        private readonly CancellationTokenSource _linkedCt; // link(用户 ct, _disposeCts)
        private bool _ctsDisposed;

        private int _state = (int)SseConnectionState.Connecting;
        private int _disposed;
        private Exception _callbackFault; // 首个用户回调异常（任一线程经 CAS 写入首个）

        public event Action<SseEvent> OnEvent;
        public event Action<Exception> OnError;
        public event Action<SseConnectionState, SseConnectionState> OnStateChanged;

        public SseConnectionState State => (SseConnectionState)Volatile.Read(ref _state);
        public Task Completion => _completion.Task;

        internal SseConnection(IHttpClient client,
            Func<CancellationToken, Task<IHttpRequest>> requestFactory,
            SseConnectionOptions options, CancellationToken ct,
            Action<SseEvent> onEvent = null,
            Action<Exception> onError = null,
            Action<SseConnectionState, SseConnectionState> onStateChanged = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));

            _options = options ?? new SseConnectionOptions();
            ValidateOptions(_options); // 非法配置 fail-fast，避免循环里静默关闭/反复报错

            // 构造期挂回调：在后台循环启动前完成订阅，从源头消除 construct-then-subscribe 竞态
            // （后续仍可用 OnEvent/OnError/OnStateChanged 的 += 追加订阅）。
            if (onEvent != null) OnEvent += onEvent;
            if (onError != null) OnError += onError;
            if (onStateChanged != null) OnStateChanged += onStateChanged;

            _rng = _options.JitterRng ?? CreateDefaultRng();
            _delay = _options.DelayProvider ?? ((d, token) => Task.Delay(d, token));

            _disposeCts = new CancellationTokenSource();
            _linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            // 在后台线程跑循环：避免捕获调用方（可能是 Unity 主线程）的 SynchronizationContext，
            // 保证所有回调都在后台线程触发（含 requestFactory 同步抛错的 OnError）。
            _ = Task.Run(RunLoopAsync);
        }

        private static Func<double> CreateDefaultRng()
        {
            // 每连接独立、去相关的种子（避免同刻创建的多连接抖动同步）。仅在循环线程调用，无需线程安全。
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            return () => rnd.NextDouble();
        }

        private static void ValidateOptions(SseConnectionOptions o)
        {
            if (o.ReconnectDelayInit < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(o), "ReconnectDelayInit 不能为负。");
            if (o.ReconnectDelayIncFn == null)
                throw new ArgumentNullException(nameof(o), "ReconnectDelayIncFn 不能为 null。");
            if (o.BackoffResetThreshold < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(o), "BackoffResetThreshold 不能为负。");
            if (o.MaxReconnectDelay is TimeSpan md && md <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(o), "MaxReconnectDelay 必须为正（null 表示不限）。");
            if (o.ReconnectJitter < 0 || o.ReconnectJitter > 1)
                throw new ArgumentOutOfRangeException(nameof(o), "ReconnectJitter 必须在 [0,1]。");
            if (o.MaxReconnectAttempts < 0)
                throw new ArgumentOutOfRangeException(nameof(o), "MaxReconnectAttempts 不能为负（0 表示不限）。");
            if (o.MaxElapsedReconnectTime is TimeSpan me && me <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(o), "MaxElapsedReconnectTime 必须为正（null 表示不限）。");
            if (o.IdleTimeout is TimeSpan idle && idle <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(o), "IdleTimeout 必须为正（null 表示不启用）。");
        }

        private async Task RunLoopAsync()
        {
            Exception lastError = null; // 最近一次失败原因（干净 EOF 为 null），用于 ShouldReconnect / 放弃异常
            int attempts = 0;           // 当前失败连续计数（建立成功后清零）
            bool exhausted = false;     // 达到次数/时长上限而放弃
            try
            {
                var delay = _options.ReconnectDelayInit; // 当前退避基准
                Stopwatch streakSw = null;               // 失败连续段计时（建立成功后清零）

                while (!_linkedCt.IsCancellationRequested)
                {
                    bool terminal = false;    // 204：服务端要求停止
                    bool hadByte = false;     // 本轮是否收到过任意 SSE body 字节
                    Stopwatch upSw = null;    // 自首字节起的存活计时（判断是否达 BackoffResetThreshold）
                    lastError = null;

                    CancellationTokenSource idleCts = null, linked = null;
                    try
                    {
                        _parser.Reset(); // 清上一连接半行/半事件/BOM（保留 LastEventId/Retry）

                        var request = await _requestFactory(_linkedCt.Token).ConfigureAwait(false);

                        var sendTok = _linkedCt.Token;
                        var idle = _options.IdleTimeout ?? TimeSpan.Zero;
                        if (idle > TimeSpan.Zero)
                        {
                            idleCts = new CancellationTokenSource(idle);
                            linked = CancellationTokenSource.CreateLinkedTokenSource(_linkedCt.Token, idleCts.Token);
                            sendTok = linked.Token;
                        }

                        // OnByte 在 worker 线程随每块字节触发。捕获本轮局部 idleCts（不再用共享字段）：
                        // 连接结束后不会再有回调，finally 里 Dispose(idleCts) 与之无并发；ODE 仅为防御性兜底。
                        void OnByte()
                        {
                            if (idle > TimeSpan.Zero)
                            {
                                try { idleCts.CancelAfter(idle); } catch (ObjectDisposedException) { }
                            }
                            if (!hadByte)
                            {
                                hadByte = true;
                                upSw = Stopwatch.StartNew();
                                SetState(SseConnectionState.Open);
                            }
                        }

                        var lastEventId = _options.AutoInjectLastEventId ? _parser.LastEventId : null;
                        using var resp = await SseCoreExtensions.RunOneConnectionAsync(
                            _client, request, _parser, RaiseEvent, OnByte, sendTok, lastEventId)
                            .ConfigureAwait(false);

                        // 非 2xx 已由 RunOneConnectionAsync 的 OnHeadersReceived 抛 SseHttpStatusException
                        // （body 到达前），不会解析出伪事件、也不会触发 OnByte（hadByte 保持 false）。到这里一定是 2xx。
                        if (resp.StatusCode == 204)
                            terminal = true; // SSE 规范：204 No Content = 服务端要求停止重连
                    }
                    catch (OperationCanceledException) when (_linkedCt.IsCancellationRequested)
                    {
                        break; // 用户 Dispose / 取消（或回调异常触发的取消）→ 退出
                    }
                    catch (OperationCanceledException)
                    {
                        lastError = new TimeoutException("SSE idle/heartbeat timeout"); // 空闲超时 → 重连
                        RaiseError(lastError);
                    }
                    catch (Exception ex)
                    {
                        if (_linkedCt.IsCancellationRequested) break; // 取消派生的异常按取消处理
                        lastError = ex; // 网络/TLS/超时(CurlHttpException) / 非 2xx / requestFactory 抛错 → 重连
                        RaiseError(ex);
                    }
                    finally
                    {
                        idleCts?.Dispose();
                        linked?.Dispose();
                    }

                    if (terminal) break;                                   // 204
                    if (_linkedCt.IsCancellationRequested) break;          // Dispose/取消
                    if (Volatile.Read(ref _callbackFault) != null) break;  // 回调抛错 → 终止（Completion fault）

                    // 「是否真正建立」只看本轮是否收到字节 + 存活是否达 BackoffResetThreshold，与连接
                    // 「如何结束」无关——干净 EOF / 网络错误 / 空闲超时 一视同仁。生产中连接绝大多数以错误或
                    // 超时断开而非干净 EOF；若仅干净 EOF 才算建立，退避会几乎永远递增到上限、放弃计数也会
                    // 误杀健康但短命的连接。非 2xx（快速失败）与空 2xx EOF 因 hadByte=false 永不算建立。
                    bool established = hadByte &&
                        (_options.BackoffResetThreshold <= TimeSpan.Zero ||
                         (upSw != null && upSw.Elapsed >= _options.BackoffResetThreshold));

                    if (_options.ShouldReconnect != null)
                    {
                        bool ok;
                        try { ok = _options.ShouldReconnect(lastError); }
                        catch (Exception ex) { FaultFromCallback(ex); break; }
                        if (!ok) break; // 用户判定停止 → 优雅结束
                    }

                    if (established)
                    {
                        delay = _options.ReconnectDelayInit; // 建立成功 → 退避/计数/计时全部重置
                        attempts = 0;
                        streakSw = null;
                    }
                    else
                    {
                        attempts++;
                        if (streakSw == null) streakSw = Stopwatch.StartNew();
                        if ((_options.MaxReconnectAttempts > 0 && attempts >= _options.MaxReconnectAttempts) ||
                            (_options.MaxElapsedReconnectTime is TimeSpan max && streakSw.Elapsed >= max))
                        {
                            exhausted = true;
                            break;
                        }
                    }

                    SetState(SseConnectionState.Reconnecting);

                    var wait = ComputeWait(delay); // retry: 优先 + 上限 clamp + jitter
                    try { await _delay(wait, _linkedCt.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    if (!established)
                        delay = _options.ReconnectDelayIncFn(delay); // 仅未建立时递增退避
                }
            }
            catch (Exception ex)
            {
                FaultFromCallback(ex); // 兜底：保证后台循环任何未预期异常都可观测，不会成为未观测 Task 异常
            }
            finally
            {
                SetState(SseConnectionState.Closed); // 终态在后台线程触发（不在 Dispose 调用线程）
                lock (_ctsGate)
                {
                    _linkedCt.Dispose();
                    _disposeCts.Dispose();
                    _ctsDisposed = true;
                }
                var cb = Volatile.Read(ref _callbackFault);
                if (cb != null) _completion.TrySetException(cb);
                else if (exhausted) _completion.TrySetException(new SseReconnectExhaustedException(attempts, lastError));
                else _completion.TrySetResult(true);
            }
        }

        /// <summary>计算本次重连的实际等待：服务端 <c>retry:</c> 优先于退避基准，再 clamp 到上限并叠加抖动。</summary>
        private TimeSpan ComputeWait(TimeSpan backoff)
        {
            TimeSpan baseDelay = _parser.RetryMilliseconds is int retry
                ? TimeSpan.FromMilliseconds(retry) // 服务端 retry: 优先
                : backoff;
            if (_options.MaxReconnectDelay is TimeSpan max && baseDelay > max)
                baseDelay = max;
            if (_options.ReconnectJitter > 0 && baseDelay > TimeSpan.Zero)
            {
                double r = _rng();
                if (r < 0) r = 0; else if (r >= 1) r = 1 - 1e-9; // 容忍越界随机源，保证 factor ∈ (1-jitter, 1]
                double factor = 1.0 - _options.ReconnectJitter * r;
                baseDelay = TimeSpan.FromTicks((long)(baseDelay.Ticks * factor));
            }
            return baseDelay < TimeSpan.Zero ? TimeSpan.Zero : baseDelay;
        }

        private void SetState(SseConnectionState next)
        {
            while (true)
            {
                int prev = Volatile.Read(ref _state);
                if (prev == (int)SseConnectionState.Closed) return; // 终态，不再变
                if (prev == (int)next) return;
                if (Interlocked.CompareExchange(ref _state, (int)next, prev) == prev)
                {
                    var h = OnStateChanged;
                    if (h != null)
                    {
                        try { h((SseConnectionState)prev, next); }
                        catch (Exception ex) { FaultFromCallback(ex); }
                    }
                    return;
                }
                // CAS 失败（并发转换）→ 重试
            }
        }

        private void RaiseEvent(SseEvent e)
        {
            var h = OnEvent;
            if (h == null) return;
            try { h(e); }
            catch (Exception ex) { FaultFromCallback(ex); }
        }

        private void RaiseError(Exception err)
        {
            var h = OnError;
            if (h == null) return;
            try { h(err); }
            catch (Exception ex) { FaultFromCallback(ex); }
        }

        /// <summary>记录首个用户回调异常并请求停止（取消在飞请求），由循环 finally 经 <see cref="Completion"/> fault 暴露。</summary>
        private void FaultFromCallback(Exception ex)
        {
            Interlocked.CompareExchange(ref _callbackFault, ex, null);
            lock (_ctsGate)
            {
                if (!_ctsDisposed)
                {
                    try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // 只取消；终态 Closed 与 CTS 释放由循环 finally 在后台线程置（遵守「回调在后台线程」契约）。
            lock (_ctsGate)
            {
                if (!_ctsDisposed)
                {
                    try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }
}
