using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
#if UNITY_5_3_OR_NEWER
using AOT;
#endif
using CurlUnity.Native;

namespace CurlUnity.Core
{
    /// <summary>
    /// curl_multi handle 封装。管理 easy handle 的生命周期、I/O 驱动和完成通知。
    ///
    /// 本类不包含线程逻辑，通过 Tick() 驱动。调用方选择驱动方式:
    ///   - 主线程: 在 MonoBehaviour.Update() 中调用 Tick()
    ///   - 后台线程: 使用 CurlBackgroundWorker，或自行在线程中调 Tick() + Poll()
    ///
    /// 线程安全: 本类自身不是线程安全的。Wakeup() 是唯一例外，可从任意线程调用。
    /// </summary>
    internal class CurlMulti : IDisposable
    {
        private readonly ICurlApi _api;
        private IntPtr _multi;
        private int _disposedFlag;
        private readonly HashSet<CurlRequest> _activeRequests = new();

        private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

        public CurlMulti()
            : this(CurlNativeApi.Instance)
        {
        }

        internal CurlMulti(ICurlApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _multi = _api.MultiInit();
            if (_multi == IntPtr.Zero)
                throw new InvalidOperationException("curl_multi_init returned null");
        }

        /// <summary>
        /// 提交请求。自动配置 write/header callback 和 PRIVATE 关联。
        /// 提交后 CurlRequest 的生命周期由 CurlMulti 管理，完成后自动 Dispose。
        /// <para>
        /// 只有处于 <see cref="CurlRequestState.Created"/> 的请求会真正被送入
        /// multi；已取消或已释放的请求会立即通过 OnComplete 以失败回调通知，
        /// 永远不会触碰已释放的 easy handle。
        /// </para>
        /// <para>
        /// 如果底层 curl_multi_add_handle 失败，request 不会停留在活跃集合中，
        /// 会同步通过 <see cref="CurlRequest.OnComplete"/> 以 FailureException
        /// 通知调用方，避免 Task 永远悬挂。
        /// </para>
        /// </summary>
        public void Send(CurlRequest request)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CurlMulti));

            // 只有 Created 状态的请求允许进入 multi。对于已 Cancelled / Disposed /
            // Completed 的请求，直接走失败回调，避免用已释放的 easy handle 调
            // curl_multi_add_handle（undefined behavior）。
            if (!request.TryTransitionState(CurlRequestState.Created, CurlRequestState.Submitted))
            {
                var state = request.State;
                var ex = state == CurlRequestState.Cancelled
                    ? (Exception)new OperationCanceledException("Request was cancelled before submission.")
                    : new InvalidOperationException(
                        $"Cannot submit CurlRequest in state {state}.");
                FailComplete(request, ex);
                return;
            }

            request.SelfHandle = GCHandle.Alloc(request);
            var ptr = GCHandle.ToIntPtr(request.SelfHandle);

            // write callback: 流式模式转发到 DataCallback，否则写入 BodyBuffer
            _api.SetOptWriteFunction(request.Handle, OnWriteData);
            _api.SetOptWriteData(request.Handle, ptr);

            // header callback: 仅在 CaptureHeaders 时设置
            if (request.CaptureHeaders)
            {
                request.HeaderBuffer = new MemoryStream(2048);
                _api.SetOptHeaderFunction(request.Handle, OnHeaderData);
                _api.SetOptHeaderData(request.Handle, ptr);
            }

            // PRIVATE 关联
            _api.SetOptPtr(request.Handle, CurlNative.CURLOPT_PRIVATE, ptr);

            // CA 证书
            CurlCerts.ApplyTo(request.Handle, _api);

            _activeRequests.Add(request);
            var rc = _api.MultiAddHandle(_multi, request.Handle);
            if (rc != CurlNative.CURLE_OK)
            {
                _activeRequests.Remove(request);
                var ex = new InvalidOperationException(
                    $"curl_multi_add_handle failed (code {rc}): {_api.GetErrorString(rc)}");
                FailComplete(request, ex);
            }
        }

        public void Tick()
        {
            if (IsDisposed) return;

            var rc = _api.MultiPerform(_multi, out _);
            if (rc != CurlNative.CURLE_OK)
                CurlLog.Warn($"curl_multi_perform returned {rc}: {_api.GetErrorString(rc)}");

            while (_api.MultiInfoRead(_multi, out var easyHandle, out var curlCode) == 1)
            {
                ProcessCompletion(easyHandle, curlCode);
            }
        }

        public void Poll(int timeoutMs)
        {
            if (IsDisposed) return;
            var rc = _api.MultiPoll(_multi, IntPtr.Zero, 0, timeoutMs, out _);
            if (rc != CurlNative.CURLE_OK)
                CurlLog.Warn($"curl_multi_poll returned {rc}: {_api.GetErrorString(rc)}");
        }

        /// <summary>线程安全，可从任意线程调用。</summary>
        public void Wakeup()
        {
            if (IsDisposed) return;
            // wakeup 失败就只是少唤醒一次 poll；下一次 poll 自然会因 timeout 返回。
            _api.MultiWakeup(_multi);
        }

        /// <summary>
        /// 取消请求。根据请求的当前状态决定具体动作：
        /// <list type="bullet">
        ///   <item><c>Created</c>: 尚未进 multi，直接标记 Cancelled 并通过 OnComplete
        ///   通知，然后 Dispose。</item>
        ///   <item><c>Submitted</c>: 已在 multi 中，先从 multi 移除再 Dispose；
        ///   OnComplete 已由 <see cref="SendAsync 路径"/>上层的 CancellationToken
        ///   回调做过 TrySetCanceled，这里只负责资源回收。</item>
        ///   <item><c>Completed</c> / <c>Cancelled</c> / <c>Disposed</c>: 无操作。</item>
        /// </list>
        /// 必须在驱动线程上调用（与 Tick 同一线程）。
        /// </summary>
        internal void Cancel(CurlRequest request)
        {
            if (IsDisposed) return;

            // 未提交就取消：直接走失败回调，不进 multi。
            if (request.TryTransitionState(CurlRequestState.Created, CurlRequestState.Cancelled))
            {
                FailComplete(request,
                    new OperationCanceledException("Request was cancelled before submission."));
                return;
            }

            // 已提交：从 multi 中拔出，释放资源。
            if (request.TryTransitionState(CurlRequestState.Submitted, CurlRequestState.Cancelled))
            {
                _activeRequests.Remove(request);
                var rc = _api.MultiRemoveHandle(_multi, request.Handle);
                if (rc != CurlNative.CURLE_OK)
                    CurlLog.Warn($"curl_multi_remove_handle on cancel returned {rc}");
                request.Dispose();
                return;
            }

            // 其它状态（Completed / Cancelled / Disposed）无操作。
        }

        /// <summary>
        /// 把请求以"失败"状态送达 OnComplete，不经过 multi。用于提交前就已失败
        /// 的路径（add_handle 失败、状态不允许提交等）。调用后释放 request 持有的
        /// 资源（easy handle、slist、buffers）。
        /// </summary>
        private void FailComplete(CurlRequest request, Exception ex)
        {
            if (request.SelfHandle.IsAllocated)
                request.SelfHandle.Free();

            var resp = new CurlResponse
            {
                FailureException = ex,
                // 失败时不转移 easy handle 所有权，下面 request.Dispose() 会清理它
            };

            try { request.OnComplete?.Invoke(resp); }
            catch (Exception cbEx) { CurlLog.Warn($"OnComplete threw during fail-complete: {cbEx}"); }

            request.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

            // 清理所有仍在执行的请求（释放 GCHandle 和 easy handle）
            foreach (var request in _activeRequests)
            {
                _api.MultiRemoveHandle(_multi, request.Handle);
                request.Dispose();
            }
            _activeRequests.Clear();

            if (_multi != IntPtr.Zero)
            {
                _api.MultiCleanup(_multi);
                _multi = IntPtr.Zero;
            }
        }

        private void ProcessCompletion(IntPtr easyHandle, int curlCode)
        {
            _api.GetInfoString(easyHandle, CurlNative.CURLINFO_PRIVATE, out var ptr);
            var request = (CurlRequest)GCHandle.FromIntPtr(ptr).Target;

            _activeRequests.Remove(request);

            _api.GetInfoLong(easyHandle, CurlNative.CURLINFO_RESPONSE_CODE, out var statusCode);

            var response = new CurlResponse
            {
                CurlCode = curlCode,
                StatusCode = statusCode,
                Body = request.DataCallback != null ? null : request.BodyBuffer.ToArray(),
                RawHeaders = request.HeaderBuffer?.ToArray(),
                EasyHandle = request.Handle  // 所有权转移
            };

            _api.MultiRemoveHandle(_multi, easyHandle);

            try { request.OnComplete?.Invoke(response); }
            catch (Exception) { /* TODO: 接入日志系统 */ }

            request.ReleaseBuffers();  // 释放辅助资源，不释放 easy handle
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.WriteCallback))]
#endif
        private static UIntPtr OnWriteData(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata)
        {
            var length = size.ToUInt64() * nmemb.ToUInt64();
            if (length == 0) return UIntPtr.Zero;
            if (length > int.MaxValue) return UIntPtr.Zero; // 超出托管内存单次处理能力，通知 curl 中止
            var totalBytes = (int)length;

            try
            {
                var request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target;
                var buffer = new byte[totalBytes];
                Marshal.Copy(ptr, buffer, 0, totalBytes);

                if (request.DataCallback != null)
                    request.DataCallback(buffer, 0, totalBytes);
                else
                    request.BodyBuffer.Write(buffer, 0, totalBytes);
            }
            catch
            {
                return UIntPtr.Zero;
            }

            return (UIntPtr)totalBytes;
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.WriteCallback))]
#endif
        private static UIntPtr OnHeaderData(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata)
        {
            var length = size.ToUInt64() * nmemb.ToUInt64();
            if (length == 0) return UIntPtr.Zero;
            if (length > int.MaxValue) return UIntPtr.Zero;
            var totalBytes = (int)length;

            try
            {
                var request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target;
                var buffer = new byte[totalBytes];
                Marshal.Copy(ptr, buffer, 0, totalBytes);
                request.HeaderBuffer?.Write(buffer, 0, totalBytes);
            }
            catch
            {
                return UIntPtr.Zero;
            }

            return (UIntPtr)totalBytes;
        }
    }
}
