using System;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;

namespace CurlUnity.Sse
{
    /// <summary>
    /// <see cref="ISseConnection"/> 的实现：在 Layer 1 <see cref="SseCoreExtensions.RunOneConnectionAsync"/>
    /// 之上跑重连循环，叠加指数退避、<c>Last-Event-ID</c> 注入、<c>retry:</c> 响应、空闲/心跳超时、状态机。
    /// 构造即开始连接（循环在后台异步运行）。
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
            _disposeCts = new CancellationTokenSource();
            _linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            _ = RunLoopAsync();
        }

        private async Task RunLoopAsync()
        {
            // 让 OpenSse/构造方有同步窗口挂回调，再触发任何事件/状态变更
            await Task.Yield();

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
                                delay = _options.ReconnectDelayInit; // 连接成功 → 重置退避
                            }
                        }

                        var lastEventId = _options.AutoInjectLastEventId ? _parser.LastEventId : null;
                        using var resp = await SseCoreExtensions.RunOneConnectionAsync(
                            _client, request, _parser, RaiseEvent, OnByte, sendTok, lastEventId)
                            .ConfigureAwait(false);

                        // 走到这 = 服务端正常关流(EOF) 或非 2xx（4xx/5xx 不抛）
                        if (resp.StatusCode < 200 || resp.StatusCode >= 300)
                            RaiseError(new SseHttpStatusException(resp.StatusCode));
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
                SetState(SseConnectionState.Closed);
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
            try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { } // 与循环 finally 的 Dispose 竞争兜底
            SetState(SseConnectionState.Closed); // 即时反馈；循环 finally 也会到 Closed（幂等）
            // 不在调用方线程 join 循环 Task，避免阻塞
        }
    }
}
