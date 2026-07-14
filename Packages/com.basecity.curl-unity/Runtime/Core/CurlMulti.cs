using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
#if UNITY_5_3_OR_NEWER
using AOT;
#endif
using CurlUnity.Http;
using CurlUnity.Diagnostics;
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
        // delegate 实例必须静态持有：libcurl 在整个传输期间保存 marshal 出的函数指针，
        // 方法组直接传参产生的临时 delegate（C# 11 之前编译器不缓存）一旦被 GC，
        // Mono 下对应 thunk 被释放，libcurl 回调即踩悬挂指针（IL2CPP 不受影响）。
        // 与 CurlCookieJar 的 s_lockCb/s_unlockCb 同一规范。
        private static readonly CurlNative.WriteCallback s_writeCb = OnWriteData;
        private static readonly CurlNative.WriteCallback s_headerCb = OnHeaderData;
        private static readonly CurlNative.WriteCallback s_readCb = OnReadData;

        private readonly ICurlApi _api;
        private readonly CurlLogger _logger;
        private IntPtr _multi;
        private int _disposedFlag;
        private readonly HashSet<CurlRequest> _activeRequests = new();

        private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

        // 连接数上限默认值。libcurl 默认两者都是 0（不限），并发 SendAsync 一多就会
        // 无上限开 socket——移动端抢占蜂窝无线电、耗尽 fd、也容易压垮服务端。默认给
        // 保守上限，超出的传输由 libcurl 内部排队等空闲连接，不会失败。
        internal const int DefaultMaxTotalConnections = 16;
        internal const int DefaultMaxHostConnections = 6;

        public CurlMulti()
            : this(CurlNativeApi.Instance, logger: null)
        {
        }

        /// <param name="maxTotalConnections">CURLMOPT_MAX_TOTAL_CONNECTIONS；0 = 不限。</param>
        /// <param name="maxHostConnections">CURLMOPT_MAX_HOST_CONNECTIONS；0 = 不限。</param>
        internal CurlMulti(ICurlApi api,
            int maxTotalConnections = DefaultMaxTotalConnections,
            int maxHostConnections = DefaultMaxHostConnections,
            CurlLogger logger = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? CurlLogger.Default;
            if (maxTotalConnections < 0)
                throw new ArgumentOutOfRangeException(nameof(maxTotalConnections), "必须 >= 0（0 = 不限）");
            if (maxHostConnections < 0)
                throw new ArgumentOutOfRangeException(nameof(maxHostConnections), "必须 >= 0（0 = 不限）");

            _multi = _api.MultiInit();
            if (_multi == IntPtr.Zero)
                throw new InvalidOperationException("curl_multi_init returned null");

            // setopt 失败不 fatal（这两个选项 libcurl 7.30+ 恒支持，防御性处理）：
            // 没有上限只是回到 libcurl 默认行为，不值得让整个 client 构造失败。
            if (maxTotalConnections > 0)
            {
                var rc = _api.MultiSetOptLong(_multi,
                    CurlNative.CURLMOPT_MAX_TOTAL_CONNECTIONS, maxTotalConnections);
                if (rc != CurlNative.CURLE_OK && _logger.IsEnabled(CurlLogLevel.Warning))
                    _logger.Warning(CurlLogCategory.Core,
                        $"CURLMOPT_MAX_TOTAL_CONNECTIONS returned {rc} ({_api.GetMultiErrorString(rc)}); connection count is unbounded.");
            }
            if (maxHostConnections > 0)
            {
                var rc = _api.MultiSetOptLong(_multi,
                    CurlNative.CURLMOPT_MAX_HOST_CONNECTIONS, maxHostConnections);
                if (rc != CurlNative.CURLE_OK && _logger.IsEnabled(CurlLogLevel.Warning))
                    _logger.Warning(CurlLogCategory.Core,
                        $"CURLMOPT_MAX_HOST_CONNECTIONS returned {rc} ({_api.GetMultiErrorString(rc)}); per-host connection count is unbounded.");
            }
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
                    _api.SetOptWriteFunction(request.Handle, s_writeCb), request)) return;
            if (!TrySetOpt("CURLOPT_WRITEDATA",
                    _api.SetOptWriteData(request.Handle, ptr), request)) return;

            // read callback: 仅在流式上传(UploadStream != null)时设置
            if (request.UploadStream != null)
            {
                if (!TrySetOpt("CURLOPT_READFUNCTION",
                        _api.SetOptReadFunction(request.Handle, s_readCb), request)) return;
                if (!TrySetOpt("CURLOPT_READDATA",
                        _api.SetOptReadData(request.Handle, ptr), request)) return;
            }

            // header callback: 捕获原始 headers 或观察完整 header block 时设置。
            // CONNECT 代理握手头不属于 origin response，交给 libcurl 在 native 层过滤。
            if (request.CaptureHeaders || request.HeadersReceivedCallback != null)
            {
                if (request.CaptureHeaders)
                    request.HeaderBuffer = new MemoryStream(2048);
                if (!TrySetOpt("CURLOPT_SUPPRESS_CONNECT_HEADERS",
                        _api.SetOptLong(request.Handle,
                            CurlNative.CURLOPT_SUPPRESS_CONNECT_HEADERS, 1), request)) return;
                if (!TrySetOpt("CURLOPT_HEADERFUNCTION",
                        _api.SetOptHeaderFunction(request.Handle, s_headerCb), request)) return;
                if (!TrySetOpt("CURLOPT_HEADERDATA",
                        _api.SetOptHeaderData(request.Handle, ptr), request)) return;
            }

            // PRIVATE 关联
            if (!TrySetOpt("CURLOPT_PRIVATE",
                    _api.SetOptPtr(request.Handle, CurlNative.CURLOPT_PRIVATE, ptr), request)) return;

            // CA 证书 —— 失败不 fatal，但会影响 TLS 验证行为；由 CurlCerts 自己日志化
            CurlCerts.ApplyTo(request.Handle, _api, request.Logger, request.RequestId);

            _activeRequests.Add(request);
            var rc = _api.MultiAddHandle(_multi, request.Handle);
            if (rc != CurlNative.CURLE_OK)
            {
                _activeRequests.Remove(request);
                var ex = CurlHttpException.SetupFailure(rc,
                    $"curl_multi_add_handle: {_api.GetMultiErrorString(rc)}");
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
            var ex = CurlHttpException.SetupFailure(rc,
                $"curl_easy_setopt({optName}): {_api.GetErrorString(rc)}");
            FailComplete(request, ex);
            return false;
        }

        public void Tick()
        {
            if (IsDisposed) return;

            var rc = _api.MultiPerform(_multi, out _);
            if (rc != CurlNative.CURLE_OK && _logger.IsEnabled(CurlLogLevel.Warning))
                _logger.Warning(CurlLogCategory.Core,
                    $"curl_multi_perform returned {rc}: {_api.GetMultiErrorString(rc)}");

            while (_api.MultiInfoRead(_multi, out var easyHandle, out var curlCode) == 1)
            {
                ProcessCompletion(easyHandle, curlCode);
            }
        }

        public void Poll(int timeoutMs)
        {
            if (IsDisposed) return;
            var rc = _api.MultiPoll(_multi, IntPtr.Zero, 0, timeoutMs, out _);
            if (rc != CurlNative.CURLE_OK && _logger.IsEnabled(CurlLogLevel.Warning))
                _logger.Warning(CurlLogCategory.Core,
                    $"curl_multi_poll returned {rc}: {_api.GetMultiErrorString(rc)}");
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
                    if (request.Logger.IsEnabled(CurlLogLevel.Error))
                        request.Logger.Error(CurlLogCategory.Core,
                            $"curl_multi_remove_handle on cancel returned {rc} ({_api.GetMultiErrorString(rc)}); " +
                            $"leaving easy handle attached to multi to avoid use-after-free. " +
                            $"It will be reclaimed when multi is disposed.",
                            requestId: request.RequestId);
                    return;
                }

                _activeRequests.Remove(request);
                request.Dispose();
                return;
            }

            // 其它状态（Completed / Cancelled / Disposed）无操作。
        }

        /// <summary>
        /// 把所有仍在 multi 中的请求以失败结束。用于驱动线程已不可继续工作
        /// （worker faulted）时的兜底，保证上层 Task 不会永久悬挂。
        /// 必须在驱动线程上调用。remove_handle 失败的 easy handle 沿用
        /// leak-over-crash 策略：保留在 _activeRequests 中等 Dispose 兜底回收。
        /// </summary>
        internal void FailAllActive(Exception cause)
        {
            if (IsDisposed) return;

            var snapshot = new CurlRequest[_activeRequests.Count];
            _activeRequests.CopyTo(snapshot);
            foreach (var request in snapshot)
            {
                var rc = _api.MultiRemoveHandle(_multi, request.Handle);
                if (rc != CurlNative.CURLE_OK)
                {
                    if (request.Logger.IsEnabled(CurlLogLevel.Error))
                        request.Logger.Error(CurlLogCategory.Core,
                            $"FailAllActive: curl_multi_remove_handle returned {rc} ({_api.GetMultiErrorString(rc)}); " +
                            "leaving easy handle attached to multi to avoid use-after-free. Completing request as failed.",
                            requestId: request.RequestId);
                    var failResp = new CurlResponse { FailureException = cause };
                    try { request.OnComplete?.Invoke(failResp); }
                    catch (Exception cbEx)
                    {
                        if (request.Logger.IsEnabled(CurlLogLevel.Warning))
                            request.Logger.Warning(CurlLogCategory.Core,
                                "OnComplete threw during fail-all.", cbEx, request.RequestId);
                    }
                    request.ReleaseBuffers();
                    continue;
                }

                _activeRequests.Remove(request);
                FailComplete(request, cause);
            }
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
            catch (Exception cbEx)
            {
                if (request.Logger.IsEnabled(CurlLogLevel.Warning))
                    request.Logger.Warning(CurlLogCategory.Core,
                        "OnComplete threw during fail-complete.", cbEx, request.RequestId);
            }

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
                    if (request.Logger.IsEnabled(CurlLogLevel.Error))
                        request.Logger.Error(CurlLogCategory.Core,
                            $"CurlMulti.Dispose: curl_multi_remove_handle returned {rc} ({_api.GetMultiErrorString(rc)}); " +
                            $"skipping easy handle cleanup for this request to avoid use-after-free. " +
                            $"Resource will be reclaimed on process exit.",
                            requestId: request.RequestId);
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
                if (_logger.IsEnabled(CurlLogLevel.Error))
                    _logger.Error(CurlLogCategory.Core,
                        $"ProcessCompletion: failed to resolve CurlRequest from easy handle " +
                        $"(CURLINFO_PRIVATE rc={rcInfo}, ptr={ptr}). Removing stray handle from multi.");
                var removeRc = _api.MultiRemoveHandle(_multi, easyHandle);
                if (removeRc != CurlNative.CURLE_OK)
                {
                    if (_logger.IsEnabled(CurlLogLevel.Error))
                        _logger.Error(CurlLogCategory.Core,
                            $"ProcessCompletion: curl_multi_remove_handle returned {removeRc} " +
                            $"({_api.GetMultiErrorString(removeRc)}) while removing stray handle from multi. " +
                            $"Handle may remain attached and leak until multi cleanup or process exit.");
                }
                return;
            }

            // GCHandle 解析加防护：ptr 非零但 handle 已被 Free / 非法时，
            // FromIntPtr 或 Target 会抛 InvalidOperationException。没有这层防护，
            // 异常会沿 Tick 一路逃到 worker 线程顶层。处理方式与上面 ptr==Zero
            // 的防御路径一致：记 error、把 stray handle 从 multi 拔出。
            CurlRequest request = null;
            try { request = (CurlRequest)GCHandle.FromIntPtr(ptr).Target; }
            catch (Exception resolveEx)
            {
                if (_logger.IsEnabled(CurlLogLevel.Error))
                    _logger.Error(CurlLogCategory.Core,
                        $"ProcessCompletion: failed to resolve CurlRequest from CURLINFO_PRIVATE " +
                        $"(ptr=0x{ptr.ToInt64():X}): {resolveEx.GetType().Name}: {resolveEx.Message}");
            }
            if (request == null)
            {
                var strayRc = _api.MultiRemoveHandle(_multi, easyHandle);
                if (strayRc != CurlNative.CURLE_OK)
                {
                    if (_logger.IsEnabled(CurlLogLevel.Error))
                        _logger.Error(CurlLogCategory.Core,
                            $"ProcessCompletion: curl_multi_remove_handle returned {strayRc} " +
                            $"({_api.GetMultiErrorString(strayRc)}) while removing unresolvable handle from multi. " +
                            $"Handle may remain attached and leak until multi cleanup or process exit.");
                }
                return;
            }

            // Remove FIRST so we can decide safely whether to transfer handle ownership.
            // 如果 MultiRemoveHandle 失败，multi 仍持有此 easy handle，下游再调
            // curl_easy_cleanup 就是 UAF。采取 leak-over-crash：让 request 留在
            // _activeRequests 里，通过 FailureException 通知上层 Task 失败，
            // easy handle 继续 attached，由 multi.Dispose（或进程退出）兜底回收。
            var rcRemove = _api.MultiRemoveHandle(_multi, easyHandle);
            if (rcRemove != CurlNative.CURLE_OK)
            {
                if (request.Logger.IsEnabled(CurlLogLevel.Error))
                    request.Logger.Error(CurlLogCategory.Core,
                        $"ProcessCompletion: curl_multi_remove_handle returned {rcRemove} " +
                        $"({_api.GetMultiErrorString(rcRemove)}); not transferring easy handle ownership. " +
                        $"Request will complete with an error and the handle will stay attached to multi.",
                        requestId: request.RequestId);

                var failResp = new CurlResponse
                {
                    FailureException = CurlHttpException.SetupFailure(rcRemove,
                        $"curl_multi_remove_handle during completion: {_api.GetMultiErrorString(rcRemove)} " +
                        $"(handle leaked to avoid use-after-free)"),
                    // EasyHandle 不转移所有权 → 保持 IntPtr.Zero
                };

                try { request.OnComplete?.Invoke(failResp); }
                catch (Exception cbEx)
                {
                    if (request.Logger.IsEnabled(CurlLogLevel.Warning))
                        request.Logger.Warning(CurlLogCategory.Core,
                            "OnComplete threw during fail-complete.", cbEx, request.RequestId);
                }

                // 释放我们持有的辅助资源（GCHandle、slist、buffers），_handleTransferred
                // 标志让后续 Dispose 跳过 EasyCleanup。request 仍留在 _activeRequests。
                request.ReleaseBuffers();
                return;
            }

            _activeRequests.Remove(request);

            _api.GetInfoLong(easyHandle, CurlNative.CURLINFO_RESPONSE_CODE, out var statusCode);

            var body = request.DataCallback != null ? null : request.BodyBuffer.ToArray();
            var rawHeaders = request.HeaderBuffer?.ToArray();

            // 无 body 兜底: HEAD / 204 等 OnWriteData 从未触发，如果用户设了回调在此补触发。
            // 仅 curlCode == OK 时触发（连接失败等 curl 错误不应触发 "headers received"）。
            if (curlCode == CurlNative.CURLE_OK
                && !request.HeadersReceivedFired
                && !request.HeaderBlockDeferred
                && request.HeadersReceivedCallback != null)
            {
                request.HeadersReceivedFired = true;
                try
                {
                    request.HeadersReceivedCallback(statusCode, rawHeaders);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref request.DownloadError, ex, null);
                }
            }

            var response = new CurlResponse
            {
                CurlCode = curlCode,
                StatusCode = statusCode,
                Body = body,
                RawHeaders = rawHeaders,
                EasyHandle = request.Handle  // 所有权转移
            };

            try { request.OnComplete?.Invoke(response); }
            catch (Exception callbackException)
            {
                if (request.Logger.IsEnabled(CurlLogLevel.Error))
                    request.Logger.Error(CurlLogCategory.Core,
                        "OnComplete threw during request completion.", callbackException,
                        request.RequestId);
            }

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

            // GCHandle resolve 失败意味着 userdata 不是我们 alloc 的 handle,或 handle 已 Free ——
            // 属于"不该发生"的内部状态异常。返回 0 让 curl 以 CURLE_WRITE_ERROR 收尾,同时
            // log 以便定位根因(否则用户只会看到一个语义含糊的 write error)。
            CurlRequest request;
            try { request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target; }
            catch (Exception resolveEx)
            {
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnWriteData: failed to resolve CurlRequest from userdata " +
                        $"(userdata=0x{userdata.ToInt64():X}): {resolveEx.GetType().Name}: {resolveEx.Message}");
                return UIntPtr.Zero;
            }
            if (request == null)
            {
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnWriteData: GCHandle.Target is null (userdata=0x{userdata.ToInt64():X})");
                return UIntPtr.Zero;
            }

            // 防御性兜底：如果 header callback 未能分类响应，则在首个 body 字节前触发。
            if (request.HeadersReceivedCallback != null
                && !request.HeadersReceivedFired
                && !request.HeaderBlockDeferred)
            {
                request.HeadersReceivedFired = true;
                try
                {
                    request.Api.GetInfoLong(
                        request.Handle, CurlNative.CURLINFO_RESPONSE_CODE, out var sc);
                    request.HeadersReceivedCallback(sc, request.HeaderBuffer?.ToArray());
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref request.DownloadError, ex, null);
                    return UIntPtr.Zero;
                }
            }

            try
            {
                // native 数据只在 write callback 期间有效；借一个托管数组完成复制，
                // DataCallback / BodyBuffer 同步消费后立即归还，避免每个 chunk 一次分配。
                var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
                try
                {
                    Marshal.Copy(ptr, rented, 0, totalBytes);
                    if (request.DataCallback != null)
                        request.DataCallback(rented, 0, totalBytes);
                    else
                        request.BodyBuffer.Write(rented, 0, totalBytes);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }
            catch (Exception ex)
            {
                // 用户 DataCallback 抛异常: 记到 request.DownloadError,OnComplete 时由上层
                // ExceptionDispatchInfo rethrow 原异常(保留栈)。与 UploadError 对称。
                // 同一请求的 DownloadError 只保留第一个(后续回调不应再被调用,做防御)。
                Interlocked.CompareExchange(ref request.DownloadError, ex, null);
                return UIntPtr.Zero; // 触发 CURLE_WRITE_ERROR,让 curl 结束请求
            }

            return (UIntPtr)totalBytes;
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.WriteCallback))]
#endif
        private static UIntPtr OnHeaderData(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata)
        {
            // Header capture 是 best-effort：本回调里的任何失败都只导致 response.Headers
            // 空缺，Body/StatusCode 不受影响。因此所有失败路径都返回 length（告诉 curl
            // "已消费"，传输继续），而不是返回 0 触发 CURLE_WRITE_ERROR 把整个请求
            // 打断成一个语义含糊的错误。与 OnWriteData（body 完整性 fatal，必须中止）
            // 刻意不同。
            var length = size.ToUInt64() * nmemb.ToUInt64();
            if (length == 0) return UIntPtr.Zero; // 消费 0 字节 == length，语义一致
            if (length > int.MaxValue)
            {
                // libcurl 单行 header 上限约 100KB，走到这里属于异常输入。此值在 32 位
                // 平台上无法用 UIntPtr 表达为"已消费"，只能中止（实际不可达）。
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnHeaderData: header block of {length} bytes exceeds int.MaxValue; aborting transfer.");
                return UIntPtr.Zero;
            }
            var totalBytes = (int)length;
            var lengthResult = (UIntPtr)totalBytes; // 所有 best-effort 失败路径都返回"已消费"

            CurlRequest request;
            try { request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target; }
            catch (Exception resolveEx)
            {
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnHeaderData: failed to resolve CurlRequest from userdata " +
                        $"(userdata=0x{userdata.ToInt64():X}): {resolveEx.GetType().Name}: {resolveEx.Message}");
                return lengthResult;
            }
            if (request == null)
            {
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnHeaderData: GCHandle.Target is null (userdata=0x{userdata.ToInt64():X})");
                return lengthResult;
            }

            byte[] buffer;
            try
            {
                buffer = new byte[totalBytes];
                Marshal.Copy(ptr, buffer, 0, totalBytes);
            }
            catch (Exception ex)
            {
                if (request.Logger.IsEnabled(CurlLogLevel.Warning))
                    request.Logger.Warning(CurlLogCategory.Core,
                        $"OnHeaderData: failed to copy a native header line; observation for this line was skipped: " +
                        $"{ex.GetType().Name}: {ex.Message}",
                        requestId: request.RequestId);
                DisableHeaderCapture(request);
                return lengthResult;
            }

            var isStatusLine = TryParseHttpStatusCode(buffer, totalBytes, out var statusCode);
            // Header 捕获是尽力而为的，并且与响应块观察彼此独立。捕获失败时丢弃不完整
            // 数据，但仍允许状态机以 rawHeaders == null 触发回调。
            if (request.HeaderBuffer != null)
            {
                try
                {
                    if (isStatusLine && !request.HeadersReceivedFired)
                        request.HeaderBuffer.SetLength(0);
                    request.HeaderBuffer.Write(buffer, 0, totalBytes);
                }
                catch (Exception ex)
                {
                    if (request.Logger.IsEnabled(CurlLogLevel.Warning))
                        request.Logger.Warning(CurlLogCategory.Core,
                            $"OnHeaderData: header capture failed and was disabled for this request " +
                            $"(response.Headers will be null): {ex.GetType().Name}: {ex.Message}",
                            requestId: request.RequestId);
                    DisableHeaderCapture(request);
                }
            }

            // 通知后继续捕获 trailers，但即使后续字节看起来像另一个响应块，
            // 也只会为已接受的响应通知一次。
            if (request.HeadersReceivedFired)
                return lengthResult;

            if (isStatusLine)
            {
                request.HeaderBlockStatusCode = statusCode;
                request.HeaderBlockHasLocation = false;
                request.HeaderBlockDeferred = false;
            }
            else if (request.HeaderBlockStatusCode != 0
                     && IsLocationHeader(buffer, totalBytes))
            {
                request.HeaderBlockHasLocation = true;
            }

            if (IsHeaderBlockTerminator(buffer, totalBytes))
                return CompleteHeaderBlock(request, lengthResult);

            return lengthResult;
        }

        private static UIntPtr CompleteHeaderBlock(CurlRequest request, UIntPtr lengthResult)
        {
            var statusCode = request.HeaderBlockStatusCode;
            if (statusCode == 0)
                return lengthResult;

            request.HeaderBlockStatusCode = 0;

            if (statusCode >= 100 && statusCode < 200)
            {
                request.HeaderBlockDeferred = true;
                request.HeaderBlockHasLocation = false;
                return lengthResult;
            }

            if (request.FollowRedirects
                && statusCode >= 300 && statusCode < 400
                && request.HeaderBlockHasLocation)
            {
                request.HeaderBlockDeferred = true;
                request.HeaderBlockHasLocation = false;
                return lengthResult;
            }

            request.HeaderBlockDeferred = false;
            request.HeaderBlockHasLocation = false;

            if (request.HeadersReceivedCallback == null)
                return lengthResult;

            request.HeadersReceivedFired = true;
            try
            {
                request.HeadersReceivedCallback(statusCode, request.HeaderBuffer?.ToArray());
                return lengthResult;
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref request.DownloadError, ex, null);
                return UIntPtr.Zero;
            }
        }

        private static void DisableHeaderCapture(CurlRequest request)
        {
            try { request.HeaderBuffer?.Dispose(); } catch { /* 尽力释放 */ }
            request.HeaderBuffer = null;
        }

        private static bool TryParseHttpStatusCode(byte[] buffer, int length, out long statusCode)
        {
            statusCode = 0;
            if (length < 9
                || buffer[0] != (byte)'H'
                || buffer[1] != (byte)'T'
                || buffer[2] != (byte)'T'
                || buffer[3] != (byte)'P'
                || buffer[4] != (byte)'/')
                return false;

            var index = 5;
            while (index < length && buffer[index] != (byte)' ')
                index++;
            while (index < length && buffer[index] == (byte)' ')
                index++;

            if (index + 2 >= length
                || buffer[index] < (byte)'0' || buffer[index] > (byte)'9'
                || buffer[index + 1] < (byte)'0' || buffer[index + 1] > (byte)'9'
                || buffer[index + 2] < (byte)'0' || buffer[index + 2] > (byte)'9')
                return false;

            statusCode = (buffer[index] - (byte)'0') * 100L
                + (buffer[index + 1] - (byte)'0') * 10L
                + buffer[index + 2] - (byte)'0';
            return true;
        }

        private static bool IsLocationHeader(byte[] buffer, int length)
        {
            const string name = "location";
            if (length < name.Length + 1 || buffer[name.Length] != (byte)':')
                return false;

            for (var i = 0; i < name.Length; i++)
            {
                var value = buffer[i];
                if (value >= (byte)'A' && value <= (byte)'Z')
                    value = (byte)(value + ('a' - 'A'));
                if (value != (byte)name[i])
                    return false;
            }

            // libcurl 会去除 Location 周围的可选空白，且不会跟随空值。这里保持相同判断，
            // 避免空 Location 导致本应作为最终响应的 3xx 响应块被延后处理。
            for (var i = name.Length + 1; i < length; i++)
            {
                var value = buffer[i];
                if (value == (byte)' ' || value == (byte)'\t')
                    continue;
                return value != (byte)'\r' && value != (byte)'\n';
            }

            return false;
        }

        private static bool IsHeaderBlockTerminator(byte[] buffer, int length)
        {
            return (length == 2 && buffer[0] == (byte)'\r' && buffer[1] == (byte)'\n')
                || (length == 1 && buffer[0] == (byte)'\n');
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

            // GCHandle resolve 失败 log 以便定位(否则用户只看到 CURLE_ABORTED_BY_CALLBACK)。
            CurlRequest request;
            try { request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target; }
            catch (Exception resolveEx)
            {
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnReadData: failed to resolve CurlRequest from userdata " +
                        $"(userdata=0x{userdata.ToInt64():X}): {resolveEx.GetType().Name}: {resolveEx.Message}");
                return (UIntPtr)CurlNative.CURL_READFUNC_ABORT;
            }
            if (request == null)
            {
                if (CurlLogger.Default.IsEnabled(CurlLogLevel.Error))
                    CurlLogger.Default.Error(CurlLogCategory.Core,
                        $"OnReadData: GCHandle.Target is null (userdata=0x{userdata.ToInt64():X})");
                return (UIntPtr)CurlNative.CURL_READFUNC_ABORT;
            }

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
