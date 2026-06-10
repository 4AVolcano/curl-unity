using System;
using System.Collections.Concurrent;
using System.Threading;
using CurlUnity.Native;

namespace CurlUnity.Core
{
    internal class CurlBackgroundWorker : IDisposable
    {
        private readonly CurlMulti _multi;
        private readonly ConcurrentQueue<CurlRequest> _pendingRequests = new();
        private readonly ConcurrentQueue<CurlRequest> _pendingCancels = new();
        private Thread _thread;
        private volatile bool _stop;
        private int _disposedFlag;
        private int _faultedFlag;
        private Exception _faultCause; // 在 Volatile.Write(_faultedFlag) 之前写入，读侧先读 flag

        private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

        /// <summary>
        /// worker 循环连续多次抛出未处理异常后进入的不可恢复状态。faulted 后：
        /// 所有在飞/排队请求已被以失败结束，后续 <see cref="Send"/> 立即失败，
        /// 线程退出。用于避免"线程静默死亡、所有 Task 永久悬挂"。
        /// </summary>
        internal bool IsFaulted => Volatile.Read(ref _faultedFlag) != 0;

        private int _pollTimeoutMs = 1000;

        /// <summary>
        /// 单次 <c>curl_multi_poll</c> 等待上限（毫秒），同时影响 <see cref="Dispose"/> 的
        /// Join 超时（= PollTimeoutMs × 2 + 500ms）。
        /// <para>
        /// 取值必须在 [0, 1_000_000] 之间（1000 秒）。设置负数或超出范围会抛
        /// <see cref="ArgumentOutOfRangeException"/>，避免后续 Join 溢出或
        /// <c>Thread.Join</c> 因负值抛异常。
        /// </para>
        /// </summary>
        public int PollTimeoutMs
        {
            get => _pollTimeoutMs;
            set
            {
                if (value < 0 || value > 1_000_000)
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"PollTimeoutMs must be in [0, 1000000]; got {value}.");
                _pollTimeoutMs = value;
            }
        }

        public CurlBackgroundWorker()
            : this(CurlNativeApi.Instance)
        {
        }

        internal CurlBackgroundWorker(ICurlApi api)
        {
            _multi = new CurlMulti(api);
        }

        public void Start()
        {
            if (_thread != null)
                throw new InvalidOperationException("Worker already started");

            _stop = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "CurlWorker"
            };
            _thread.Start();
        }

        public void Send(CurlRequest request)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CurlBackgroundWorker));
            if (IsFaulted)
            {
                FailRequest(request, WorkerFaultedException(_faultCause));
                return;
            }
            _pendingRequests.Enqueue(request);
            _multi.Wakeup();
            // 与 EnterFaultedState 的竞态兜底：fault 恰好发生在上面的检查与入队之间时，
            // 队列已无人消费；这里再查一次并代为排空，保证请求不会永久悬挂。
            if (IsFaulted)
                DrainPendingAsFaulted();
        }

        /// <summary>
        /// 取消请求。线程安全，可从任意线程调用。
        /// 实际的 remove_handle + Dispose 在后台线程执行。
        /// </summary>
        public void Cancel(CurlRequest request)
        {
            if (IsDisposed) return;
            // faulted 后请求已全部以失败结束（或将由 Send 的兜底排空），无需取消。
            if (IsFaulted) return;
            _pendingCancels.Enqueue(request);
            _multi.Wakeup();
        }

        /// <summary>
        /// 释放 worker 线程和关联的 curl multi handle。
        /// <para>
        /// 正常情况下，<see cref="_multi.Wakeup"/> + worker 循环里的 <c>_stop</c>
        /// 检查能让线程在下一次 poll 超时（<see cref="PollTimeoutMs"/> 毫秒）
        /// 之内自然退出。我们给一个相当于 <c>2 × PollTimeoutMs + 500ms</c> 的
        /// Join 超时作为上限。
        /// </para>
        /// <para>
        /// 如果超时仍未退出，<b>几乎可以确定用户的 <c>OnDataReceived</c> 回调处于
        /// 阻塞状态</b>——libcurl 自身的 <c>curl_multi_poll</c> 严格遵守其
        /// timeoutMs，<c>curl_multi_perform</c> 的唯一长耗时来源就是用户回调。
        /// 此时强行调 <c>curl_multi_cleanup</c> 会与仍在执行的回调线程竞争同一
        /// multi/easy handle，产生 use-after-free。为此我们选择 <b>跳过 cleanup</b>
        /// 并记录一条错误日志——multi handle 由 OS 在进程退出时回收。
        /// </para>
        /// <para>
        /// 契约：<c>CurlUnity.Http.IHttpRequest.OnDataReceived</c> 必须快速返回。
        /// </para>
        /// </summary>
        /// <summary>
        /// <see cref="Dispose"/> 完成后，标识 worker 线程是否在 Join 超时前干净退出。
        /// <c>false</c> 表示发生了"用户回调阻塞 + 跳过 cleanup"分支——调用方
        /// 若依赖线程已不再访问 libcurl（例如准备 curl_global_cleanup），应据此
        /// 跳过那些全局层的清理，以免与仍在 libcurl 内部的 worker 线程竞争。
        /// Dispose 未被调用时值为 <c>true</c>。
        /// </summary>
        internal bool WorkerExitedCleanly { get; private set; } = true;

        public void Dispose()
        {
            // Interlocked 保证并发调用只有一个进入清理分支。
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

            _stop = true;
            _multi.Wakeup();

            var joinTimeout = PollTimeoutMs * 2 + 500;
            var workerExited = _thread?.Join(joinTimeout) ?? true;
            WorkerExitedCleanly = workerExited;

            // 排空未提交到 multi 的请求
            while (_pendingRequests.TryDequeue(out var request))
                request.Dispose();

            // 排空未处理的取消（对应请求仍在 multi 中，由 multi.Dispose 清理）
            while (_pendingCancels.TryDequeue(out _)) { }

            if (workerExited)
            {
                _multi.Dispose();
            }
            else
            {
                CurlLog.Error(
                    $"CurlBackgroundWorker.Dispose: worker thread did not exit within {joinTimeout}ms. " +
                    "This usually indicates a user callback (e.g. HttpRequest.OnDataReceived) is blocking. " +
                    "Skipping curl_multi_cleanup to avoid use-after-free — the multi handle will be reclaimed by the OS on process exit. " +
                    "User callbacks must return promptly; long-running work should be dispatched to another thread.");
            }
        }

        private void Run()
        {
            // 连续失败上限：单次异常视为可恢复（吞掉继续跑），连续多次说明内部状态
            // 已不可信，转入 faulted——fail 掉所有请求后退出线程。没有这层保护时，
            // 任何逃逸异常都会让线程静默死亡，该 client 的所有 Task 永久悬挂。
            const int maxConsecutiveFailures = 5;
            int consecutiveFailures = 0;

            while (!_stop)
            {
                try
                {
                    // 在每次内层处理前检查 _stop，shutdown 时不再把剩余队列推进 multi。
                    while (!_stop && _pendingRequests.TryDequeue(out var request))
                        _multi.Send(request);

                    while (!_stop && _pendingCancels.TryDequeue(out var request))
                        _multi.Cancel(request);

                    _multi.Tick();

                    // Tick 后、进入 Poll 前再检查一次：如果 Dispose 已经设了 _stop，
                    // 跳过接下来这次 Poll（也为 wakeup 万一失效提供一道保险）。
                    if (_stop) break;

                    _multi.Poll(PollTimeoutMs);
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    CurlLog.Error(
                        $"CurlBackgroundWorker: unhandled exception in worker loop " +
                        $"({consecutiveFailures}/{maxConsecutiveFailures}): {ex}");
                    if (consecutiveFailures >= maxConsecutiveFailures && !_stop)
                    {
                        EnterFaultedState(ex);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 进入不可恢复的 faulted 状态（仅在 worker 线程调用）：fail 掉 multi 内
        /// 所有在飞请求与队列里未提交的请求，使上层 Task 以异常完成而不是悬挂；
        /// 之后线程退出，后续 Send 走快速失败。
        /// </summary>
        private void EnterFaultedState(Exception cause)
        {
            _faultCause = cause;
            Volatile.Write(ref _faultedFlag, 1);
            CurlLog.Error(
                "CurlBackgroundWorker: entering faulted state — failing all in-flight and pending requests; " +
                "subsequent sends on this client will fail fast. See the errors above for the root cause.");

            try
            {
                _multi.FailAllActive(WorkerFaultedException(cause));
            }
            catch (Exception ex)
            {
                CurlLog.Error($"CurlBackgroundWorker: FailAllActive threw during fault handling: {ex}");
            }

            DrainPendingAsFaulted();
        }

        /// <summary>排空未提交的请求队列并逐个以失败结束。faulted 后可从任意线程调用。</summary>
        private void DrainPendingAsFaulted()
        {
            while (_pendingRequests.TryDequeue(out var request))
                FailRequest(request, WorkerFaultedException(_faultCause));
            while (_pendingCancels.TryDequeue(out _)) { }
        }

        private static void FailRequest(CurlRequest request, Exception ex)
        {
            var resp = new CurlResponse { FailureException = ex };
            try { request.OnComplete?.Invoke(resp); }
            catch (Exception cbEx) { CurlLog.Warn($"OnComplete threw during fault-complete: {cbEx}"); }
            request.Dispose();
        }

        private static InvalidOperationException WorkerFaultedException(Exception cause) =>
            new InvalidOperationException(
                "curl-unity background worker has faulted and can no longer process requests on this client; " +
                "the request was aborted. Dispose this client and create a new one. " +
                "See earlier [curl-unity] error logs for the root cause.",
                cause);
    }
}
