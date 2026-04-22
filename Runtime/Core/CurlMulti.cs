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
        /// <para>
        /// 提交后 CurlRequest 的活跃生命周期由 CurlMulti 管理。成功完成时
        /// 状态会进入 <see cref="CurlRequestState.Completed"/> 并通过
        /// <c>ReleaseBuffers</c> 释放辅助资源（GCHandle、slist、buffer），
        /// 但 easy handle 的所有权已转移给 <see cref="CurlResponse.EasyHandle"/>，
        /// 由调用方在消费完响应后 Dispose。<b>正常完成路径不会自动把
        /// request 转入 Disposed 状态</b>——调用方仅在希望彻底清理中途未
        /// 提交 / 被取消的 request 时需要调 <see cref="CurlRequest.Dispose"/>。
        /// </para>
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
            if (!TrySetOpt("CURLOPT_WRITEFUNCTION",
                    _api.SetOptWriteFunction(request.Handle, OnWriteData), request)) return;
            if (!TrySetOpt("CURLOPT_WRITEDATA",
                    _api.SetOptWriteData(request.Handle, ptr), request)) return;

            // read callback: 仅在流式上传(UploadStream != null)时设置
            if (request.UploadStream != null)
            {
                if (!TrySetOpt("CURLOPT_READFUNCTION",
                        _api.SetOptReadFunction(request.Handle, OnReadData), request)) return;
                if (!TrySetOpt("CURLOPT_READDATA",
                        _api.SetOptReadData(request.Handle, ptr), request)) return;
            }

            // header callback: 仅在 CaptureHeaders 时设置
            if (request.CaptureHeaders)
            {
                request.HeaderBuffer = new MemoryStream(2048);
                if (!TrySetOpt("CURLOPT_HEADERFUNCTION",
                        _api.SetOptHeaderFunction(request.Handle, OnHeaderData), request)) return;
                if (!TrySetOpt("CURLOPT_HEADERDATA",
                        _api.SetOptHeaderData(request.Handle, ptr), request)) return;
            }

            // PRIVATE 关联
            if (!TrySetOpt("CURLOPT_PRIVATE",
                    _api.SetOptPtr(request.Handle, CurlNative.CURLOPT_PRIVATE, ptr), request)) return;

            // CA 证书 —— 失败不 fatal，但会影响 TLS 验证行为；由 CurlCerts 自己日志化
            CurlCerts.ApplyTo(request.Handle, _api);

            _activeRequests.Add(request);
            var rc = _api.MultiAddHandle(_multi, request.Handle);
            if (rc != CurlNative.CURLE_OK)
            {
                _activeRequests.Remove(request);
                var ex = new InvalidOperationException(
                    $"curl_multi_add_handle failed (code {rc}): {_api.GetMultiErrorString(rc)}");
                FailComplete(request, ex);
            }
        }

        /// <summary>
        /// 检查 multi 内部 setopt 调用的返回值。失败时走 FailComplete 并返回 false，
        /// 调用方据此 return 终止 Send，避免把一个半配置好的 request 送进 multi。
        /// </summary>
        private bool TrySetOpt(string optName, int rc, CurlRequest request)
        {
            if (rc == CurlNative.CURLE_OK) return true;

            // 这里还没进 _activeRequests，不需要 Remove
            var ex = new InvalidOperationException(
                $"curl_easy_setopt({optName}) failed (code {rc}): {_api.GetErrorString(rc)}");
            FailComplete(request, ex);
            return false;
        }

        public void Tick()
        {
            if (IsDisposed) return;

            var rc = _api.MultiPerform(_multi, out _);
            if (rc != CurlNative.CURLE_OK)
                CurlLog.Warn($"curl_multi_perform returned {rc}: {_api.GetMultiErrorString(rc)}");

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
                CurlLog.Warn($"curl_multi_poll returned {rc}: {_api.GetMultiErrorString(rc)}");
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
        ///   OnComplete 已由 <c>CurlHttpClient.SendAsync</c> 路径上层的
        ///   <c>CancellationToken</c> 回调做过 <c>TrySetCanceled</c>，这里只负责
        ///   资源回收。</item>
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

            // 已提交：先尝试从 multi 中拔出；只有成功后才能安全释放 easy handle 资源。
            if (request.TryTransitionState(CurlRequestState.Submitted, CurlRequestState.Cancelled))
            {
                var rc = _api.MultiRemoveHandle(_multi, request.Handle);
                if (rc != CurlNative.CURLE_OK)
                {
                    // Remove 失败说明 multi 可能仍持有这个 easy handle。这时 Dispose 会
                    // 调 curl_easy_cleanup，释放一个 multi 还在用的 handle → UAF。
                    // 采用"泄漏优于崩溃"策略：保留 request 在 _activeRequests 里，等
                    // _multi.Dispose 时再尝试清理；如果那时仍失败，handle 就随进程退出
                    // 由 OS 回收。
                    CurlLog.Error(
                        $"curl_multi_remove_handle on cancel returned {rc} ({_api.GetMultiErrorString(rc)}); " +
                        $"leaving easy handle attached to multi to avoid use-after-free. " +
                        $"It will be reclaimed when multi is disposed.");
                    return;
                }

                _activeRequests.Remove(request);
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

            // 清理所有仍在执行的请求（释放 GCHandle 和 easy handle）。
            // 和 Cancel 路径同样的约束：只有 curl_multi_remove_handle 成功后，
            // 才能安全地 curl_easy_cleanup 对应的 easy handle——否则 multi 仍
            // 持有 handle 引用时 cleanup 会触发 UAF。失败时走"泄漏优于崩溃"：
            // 记 error 日志，留给后面 curl_multi_cleanup 或进程退出回收。
            foreach (var request in _activeRequests)
            {
                var rc = _api.MultiRemoveHandle(_multi, request.Handle);
                if (rc == CurlNative.CURLE_OK)
                {
                    request.Dispose();
                }
                else
                {
                    CurlLog.Error(
                        $"CurlMulti.Dispose: curl_multi_remove_handle returned {rc} ({_api.GetMultiErrorString(rc)}); " +
                        $"skipping easy handle cleanup for this request to avoid use-after-free. " +
                        $"Resource will be reclaimed on process exit.");
                }
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
            // CURLINFO_PRIVATE 是我们在 Send 时通过 CURLOPT_PRIVATE 设置的
            // GCHandle 指针。这里取回它来定位 CurlRequest；取不到就意味着
            // multi 传进来一个不由我们管理的 easy handle（不该发生，防御）
            // —— 记录告警并直接 remove 该 handle，避免把 null/garbage 指针
            // 喂给 GCHandle.FromIntPtr 导致 crash。
            var rcInfo = _api.GetInfoString(easyHandle, CurlNative.CURLINFO_PRIVATE, out var ptr);
            if (rcInfo != CurlNative.CURLE_OK || ptr == IntPtr.Zero)
            {
                CurlLog.Error(
                    $"ProcessCompletion: failed to resolve CurlRequest from easy handle " +
                    $"(CURLINFO_PRIVATE rc={rcInfo}, ptr={ptr}). Removing stray handle from multi.");
                var removeRc = _api.MultiRemoveHandle(_multi, easyHandle);
                if (removeRc != CurlNative.CURLE_OK)
                {
                    CurlLog.Error(
                        $"ProcessCompletion: curl_multi_remove_handle returned {removeRc} " +
                        $"({_api.GetMultiErrorString(removeRc)}) while removing stray handle from multi. " +
                        $"Handle may remain attached and leak until multi cleanup or process exit.");
                }
                return;
            }

            var request = (CurlRequest)GCHandle.FromIntPtr(ptr).Target;

            // Remove FIRST so we can decide safely whether to transfer handle ownership.
            // 如果 MultiRemoveHandle 失败，multi 仍持有此 easy handle，下游再调
            // curl_easy_cleanup 就是 UAF。采取 leak-over-crash：让 request 留在
            // _activeRequests 里，通过 FailureException 通知上层 Task 失败，
            // easy handle 继续 attached，由 multi.Dispose（或进程退出）兜底回收。
            var rcRemove = _api.MultiRemoveHandle(_multi, easyHandle);
            if (rcRemove != CurlNative.CURLE_OK)
            {
                CurlLog.Error(
                    $"ProcessCompletion: curl_multi_remove_handle returned {rcRemove} " +
                    $"({_api.GetMultiErrorString(rcRemove)}); not transferring easy handle ownership. " +
                    $"Request will complete with an error and the handle will stay attached to multi.");

                var failResp = new CurlResponse
                {
                    FailureException = new InvalidOperationException(
                        $"curl_multi_remove_handle returned {rcRemove} during completion. " +
                        $"Handle leaked to avoid use-after-free."),
                    // EasyHandle 不转移所有权 → 保持 IntPtr.Zero
                };

                try { request.OnComplete?.Invoke(failResp); }
                catch (Exception cbEx) { CurlLog.Warn($"OnComplete threw during fail-complete: {cbEx}"); }

                // 释放我们持有的辅助资源（GCHandle、slist、buffers），_handleTransferred
                // 标志让后续 Dispose 跳过 EasyCleanup。request 仍留在 _activeRequests。
                request.ReleaseBuffers();
                return;
            }

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

                // FOLLOWLOCATION=1 下，libcurl 会为重定向链里每一次响应（中间
                // 3xx 和最终响应）分别调用 header callback。每次响应都以一行
                // 状态行 "HTTP/x.y NNN ..." 开头（HTTP/2、HTTP/3 也一样，libcurl
                // 会合成相同形式的状态行送进来）。
                //
                // 遇到状态行就清空 HeaderBuffer，保证调用方看到的是最终响应
                // 的 headers，而不是所有 hop 的拼接。HeaderBuffer 可能为 null
                // （用户不关心响应头），此时只需略过写入。
                //
                // 依据: CURLOPT_HEADERFUNCTION 文档明确承诺
                //   1) 每次调用送入一行完整 header
                //   2) 状态行和 header 段尾的空行也算"header"
                //   3) 回调会被所有响应调用而不只是最终响应
                // HTTP header field-name（RFC 7230 token）不允许包含 '/'，所以
                // 行首 5 字节等于 "HTTP/" 可以唯一识别状态行。
                if (request.HeaderBuffer != null)
                {
                    if (IsHttpStatusLine(buffer, totalBytes))
                        request.HeaderBuffer.SetLength(0);
                    request.HeaderBuffer.Write(buffer, 0, totalBytes);
                }
            }
            catch
            {
                return UIntPtr.Zero;
            }

            return (UIntPtr)totalBytes;
        }

        private static bool IsHttpStatusLine(byte[] buffer, int length)
        {
            return length >= 5
                && buffer[0] == (byte)'H'
                && buffer[1] == (byte)'T'
                && buffer[2] == (byte)'T'
                && buffer[3] == (byte)'P'
                && buffer[4] == (byte)'/';
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.WriteCallback))]
#endif
        private static UIntPtr OnReadData(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata)
        {
            // libcurl 向 ptr 指向的 buffer 索要最多 size*nmemb 字节;
            // 返回 0 = EOF, CURL_READFUNC_ABORT = 中止, 正数 = 实际写入字节数
            var capacity = size.ToUInt64() * nmemb.ToUInt64();
            if (capacity == 0) return UIntPtr.Zero;
            // 防御: 超大请求拒绝而不是分配 2GB。libcurl 内部 buffer 一般 16KB,
            // 超过 int.MaxValue 属于异常输入,直接 ABORT。
            if (capacity > int.MaxValue)
                return (UIntPtr)CurlNative.CURL_READFUNC_ABORT;
            var want = (int)capacity;

            CurlRequest request;
            try { request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target; }
            catch { return (UIntPtr)CurlNative.CURL_READFUNC_ABORT; }

            // 取消竞态:已 Cancelled 直接中止,避免继续读 stream(stream 可能已被 client Dispose)
            if (request.State == CurlRequestState.Cancelled)
                return (UIntPtr)CurlNative.CURL_READFUNC_ABORT;

            // ArrayPool 复用中间缓冲,避免大文件上传时频繁 GC
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(want);
            try
            {
                int read = request.UploadStream.Read(buf, 0, want);
                if (read <= 0) return UIntPtr.Zero; // EOF
                Marshal.Copy(buf, 0, ptr, read);
                return (UIntPtr)read;
            }
            catch (Exception ex)
            {
                // Stream.Read 异常: 记录到 request,OnComplete 时由上层外抛。
                // 同一请求的 UploadError 只保留第一个(后续回调不应再被调用,但做防御)
                System.Threading.Interlocked.CompareExchange(
                    ref request.UploadError, ex, null);
                return (UIntPtr)CurlNative.CURL_READFUNC_ABORT;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }
}
