using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CurlUnity.Core
{
    internal class CurlBackgroundWorker : IDisposable
    {
        private readonly CurlMulti _multi = new();
        private readonly ConcurrentQueue<CurlRequest> _pendingRequests = new();
        private readonly ConcurrentQueue<CurlRequest> _pendingCancels = new();
        private Thread _thread;
        private volatile bool _stop;
        private bool _disposed;

        public int PollTimeoutMs { get; set; } = 1000;

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
            if (_disposed) throw new ObjectDisposedException(nameof(CurlBackgroundWorker));
            _pendingRequests.Enqueue(request);
            _multi.Wakeup();
        }

        /// <summary>
        /// 取消请求。线程安全，可从任意线程调用。
        /// 实际的 remove_handle + Dispose 在后台线程执行。
        /// </summary>
        public void Cancel(CurlRequest request)
        {
            if (_disposed) return;
            _pendingCancels.Enqueue(request);
            _multi.Wakeup();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            _multi.Wakeup();
            _thread?.Join(3000);

            // 排空未提交到 multi 的请求
            while (_pendingRequests.TryDequeue(out var request))
                request.Dispose();

            // 排空未处理的取消（对应请求仍在 multi 中，由 multi.Dispose 清理）
            while (_pendingCancels.TryDequeue(out _)) { }

            _multi.Dispose();
        }

        private void Run()
        {
            while (!_stop)
            {
                while (_pendingRequests.TryDequeue(out var request))
                    _multi.Send(request);

                while (_pendingCancels.TryDequeue(out var request))
                    _multi.Cancel(request);

                _multi.Tick();
                _multi.Poll(PollTimeoutMs);
            }
        }
    }
}
