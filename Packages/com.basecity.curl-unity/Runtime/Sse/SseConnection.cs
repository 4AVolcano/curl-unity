using System;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;

namespace CurlUnity.Sse
{
    /// <summary>
    /// <see cref="ISseConnection"/> 的实现：在 Layer 1 <see cref="SseCoreExtensions.RunOneConnectionAsync"/>
    /// 之上跑重连循环，叠加指数退避、<c>Last-Event-ID</c> 注入、<c>retry:</c> 响应、空闲/心跳超时、状态机。
    /// 构造即开始连接（循环在后台线程运行）。
    /// </summary>
    internal sealed class SseConnection : ISseConnection
    {
        private readonly IHttpClient _client;
        private readonly Func<CancellationToken, Task<IHttpRequest>> _requestFactory;
        private readonly SseConnectionOptions _options;
        private readonly CancellationTokenSource _disposeCts;
        private readonly CancellationTokenSource _linkedCt; // link(用户 ct, _disposeCts)
        private readonly SseEventParser _parser = new SseEventParser();

        private CancellationTokenSource _idleCts; // 当前轮的空闲超时 cts（onByteReceived 在其上滑动）
        private int _state = (int)SseConnectionState.Connecting;
        private int _disposed;

        public event Action<SseEvent> OnEvent;
        public event Action<Exception> OnError;
        public event Action<SseConnectionState, SseConnectionState> OnStateChanged;

        public SseConnectionState State => (SseConnectionState)Volatile.Read(ref _state);

        internal SseConnection(IHttpClient client,
            Func<CancellationToken, Task<IHttpRequest>> requestFactory,
            SseConnectionOptions options, CancellationToken ct)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));

            _options = options ?? new SseConnectionOptions();
            // 构造时校验 options，非法配置 fail-fast，避免循环里静默关闭/反复报错
            if (_options.ReconnectDelayInit < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "ReconnectDelayInit 不能为负。");
            if (_options.ReconnectDelayIncFn == null)
                throw new ArgumentNullException(nameof(options), "ReconnectDelayIncFn 不能为 null。");
            if (_options.IdleTimeout is TimeSpan idle && idle <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "IdleTimeout 必须为正（null 表示不启用）。");

            _disposeCts = new CancellationTokenSource();
            _linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            // 在后台线程跑循环：避免捕获调用方（可能是 Unity 主线程）的 SynchronizationContext，
            // 保证所有回调都在后台线程触发（含 requestFactory 同步抛错的 OnError）。
            _ = Task.Run(RunLoopAsync);
        }

        private async Task RunLoopAsync()
        {
            var delay = _options.ReconnectDelayInit;
            try
            {
                while (!_linkedCt.IsCancellationRequested)
                {
                    CancellationTokenSource idleCts = null, linked = null;
                    try
                    {
                        _parser.Reset(); // 清上一连接半行/半事件/BOM（保留 LastEventId/Retry）

                        var request = await _requestFactory(_linkedCt.Token).ConfigureAwait(false);

                        bool hadByte = false;
                        var sendTok = _linkedCt.Token;
                        var hb = TimeSpan.Zero;
                        if (_options.IdleTimeout is TimeSpan t)
                        {
                            hb = t;
                            idleCts = new CancellationTokenSource(t);
                            _idleCts = idleCts;
                            linked = CancellationTokenSource.CreateLinkedTokenSource(_linkedCt.Token, idleCts.Token);
                            sendTok = linked.Token;
                        }

                        void OnByte()
                        {
                            if (hb > TimeSpan.Zero)
                            {
                                try { _idleCts?.CancelAfter(hb); } catch (ObjectDisposedException) { }
                            }
                            if (!hadByte)
                            {
                                hadByte = true;
                                SetState(SseConnectionState.Open);
                            }
                        }

                        var lastEventId = _options.AutoInjectLastEventId ? _parser.LastEventId : null;
                        using var resp = await SseCoreExtensions.RunOneConnectionAsync(
                            _client, request, _parser, RaiseEvent, OnByte, sendTok, lastEventId)
                            .ConfigureAwait(false);

                        // 非 2xx 已由 RunOneConnectionAsync 的 OnHeadersReceived 抛
                        // SseHttpStatusException（body 到达前），不会解析出伪事件。
                        // 到这里一定是 2xx。
                        if (resp.StatusCode == 204)
                            break; // SSE 规范：204 No Content = 服务端要求停止重连
                        delay = _options.ReconnectDelayInit;
                    }
                    catch (OperationCanceledException) when (_linkedCt.IsCancellationRequested)
                    {
                        break; // 用户 Dispose / 取消 → 退出
                    }
                    catch (OperationCanceledException)
                    {
                        RaiseError(new TimeoutException("SSE idle/heartbeat timeout")); // 空闲超时 → 重连
                    }
                    catch (Exception ex)
                    {
                        RaiseError(ex); // 网络/TLS/超时(CurlHttpException) 或 requestFactory 抛错 → 重连
                    }
                    finally
                    {
                        _idleCts = null;
                        idleCts?.Dispose();
                        linked?.Dispose();
                    }

                    if (_linkedCt.IsCancellationRequested) break;

                    SetState(SseConnectionState.Reconnecting);

                    var d = _parser.RetryMilliseconds is int retry
                        ? TimeSpan.FromMilliseconds(retry) // 服务端 retry: 优先
                        : delay;
                    try { await Task.Delay(d, _linkedCt.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    delay = _options.ReconnectDelayIncFn(delay); // 递增退避
                }
            }
            finally
            {
                SetState(SseConnectionState.Closed); // 终态在后台线程触发（不在 Dispose 调用线程）
                _linkedCt.Dispose();
                _disposeCts.Dispose();
            }
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
                    h?.Invoke((SseConnectionState)prev, next);
                    return;
                }
                // CAS 失败（并发转换）→ 重试
            }
        }

        private void RaiseEvent(SseEvent e)
        {
            var h = OnEvent;
            h?.Invoke(e);
        }

        private void RaiseError(Exception ex)
        {
            var h = OnError;
            h?.Invoke(ex);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // 只取消；终态 Closed 由循环 finally 在后台线程置（遵守「回调在后台线程」契约）。
            try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
