using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Diagnostics;
using CurlUnity.Native;

namespace CurlUnity.Http
{
    /// <summary>
    /// libcurl 的 HTTP 客户端实现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>代理行为：</b>默认不使用代理，且显式屏蔽 libcurl 对
    /// <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c> 等环境变量的自动读取，避免
    /// 开发/宿主机上的代理泄漏到业务网络。需要走代理时调用
    /// <see cref="SetProxy"/> 显式激活；<see cref="ClearProxy"/> 恢复默认行为。
    /// 注意 HTTP/3 无法经 HTTP 代理隧道化，启用代理后 libcurl 会回退到 HTTP/2。
    /// </para>
    /// <para>
    /// <b>Cookie 行为：</b><see cref="IHttpRequest.EnableCookies"/> 为 <c>true</c>
    /// 的请求接入本 client 的 cookie jar（基于 <c>CURLSH</c>），跨请求持久化。
    /// 不同 <c>CurlHttpClient</c> 实例各自持有独立 jar，互不共享。<c>EnableCookies=false</c>
    /// 的请求既不读也不写 jar。jar 为纯内存存储，client Dispose 后清空。
    /// </para>
    /// </remarks>
    public class CurlHttpClient : IHttpClient
    {
        private readonly ICurlApi _api;
        private readonly CurlBackgroundWorker _worker;
        private readonly ConcurrentDictionary<IntPtr, CancellationTokenRegistration> _cancellations = new();
        private readonly ConcurrentDictionary<TaskCompletionSource<IHttpResponse>, byte> _pendingTasks = new();
        private CurlCookieJar _cookieJar;  // lazy：首次用到 EnableCookies 时初始化
        private volatile HttpProxy _proxy; // null = 不走代理（默认）；引用赋值天然原子
        private int _disposedFlag;

        private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

        /// <summary>全局 HTTP 版本偏好。对所有后续请求生效。默认 PreferH3。</summary>
        public HttpVersion PreferredVersion { get; set; } = HttpVersion.PreferH3;

        /// <summary>是否验证 SSL 证书。默认 true。</summary>
        public bool VerifySSL { get; set; } = true;

        /// <summary>诊断统计。构造时 enableDiagnostics=true 才可用，否则为 null。</summary>
        public HttpDiagnostics Diagnostics { get; }

        public CurlHttpClient(bool enableDiagnostics = false)
            : this(CurlNativeApi.Instance, enableDiagnostics)
        {
        }

        internal CurlHttpClient(ICurlApi api, bool enableDiagnostics = false)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            CurlGlobal.Acquire(_api);
            CurlCerts.Initialize();
            if (enableDiagnostics)
                Diagnostics = new HttpDiagnostics();
            _worker = new CurlBackgroundWorker(_api);
            _worker.Start();
        }

        public void SetProxy(HttpProxy proxy)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CurlHttpClient));
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        }

        public void ClearProxy()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CurlHttpClient));
            _proxy = null;
        }

        public Task<IHttpResponse> SendAsync(IHttpRequest request, CancellationToken ct = default)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CurlHttpClient));

            // 短路已取消的 token：直接以 Canceled 完成 Task，不分配 CurlRequest，
            // 也不走 ct.Register 的"注册回调可能同步触发"路径——那条路径在 token
            // 已取消时会在字典还没写入的窗口里触发回调，虽然最终会靠 OnComplete
            // 兜底清理，但逻辑绕且依赖多机制串联。明确的前置检查更清晰。
            if (ct.IsCancellationRequested)
            {
                var canceled = new TaskCompletionSource<IHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                canceled.TrySetCanceled(ct);
                return canceled.Task;
            }

            var tcs = new TaskCompletionSource<IHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

            CurlRequest curlReq;
            try
            {
                curlReq = BuildCurlRequest(request);
            }
            catch (Exception ex)
            {
                // URL / setopt / slist_append 等前置配置失败，直接让 Task 以异常完成，
                // 不进入 worker 队列。
                tcs.TrySetException(ex);
                return tcs.Task;
            }

            _pendingTasks[tcs] = 0;

            // CancellationToken → 取消请求（提交到后台线程执行 remove_handle）
            if (ct.CanBeCanceled)
            {
                var reg = ct.Register(() =>
                {
                    if (tcs.TrySetCanceled(ct))
                    {
                        _pendingTasks.TryRemove(tcs, out _);
                        if (_cancellations.TryRemove(curlReq.Handle, out var r))
                            r.Dispose();
                        _worker.Cancel(curlReq);
                    }
                });
                _cancellations[curlReq.Handle] = reg;
            }

            curlReq.OnComplete = curlResp =>
            {
                if (_cancellations.TryRemove(curlReq.Handle, out var reg))
                    reg.Dispose();

                try
                {
                    // Pipeline 未到 libcurl 就失败的路径（add_handle 失败、非法状态提交等），
                    // 直接以异常 complete Task，不构造 HttpResponse。
                    if (curlResp.FailureException != null)
                    {
                        tcs.TrySetException(curlResp.FailureException);
                        return;
                    }

                    var response = new HttpResponse(_api, curlResp);

                    // 流式上传:READFUNCTION 里 Stream.Read 抛异常会被记到 UploadError;
                    // 这里检测到就放弃 response(释放 handle),把原异常透传给调用方。
                    if (curlReq.UploadError != null)
                    {
                        response.Dispose();
                        tcs.TrySetException(curlReq.UploadError);
                        return;
                    }

                    Diagnostics?.Record(response);

                    if (!tcs.TrySetResult(response))
                        response.Dispose();  // 取消赢得竞态时释放 handle
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    _pendingTasks.TryRemove(tcs, out _);
                }
            };

            _worker.Send(curlReq);
            return tcs.Task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
            _worker.Dispose();

            // Worker 已停止，所有未完成的 Task 设置异常
            var ex = new ObjectDisposedException(nameof(CurlHttpClient));
            foreach (var kv in _pendingTasks)
                kv.Key.TrySetException(ex);
            _pendingTasks.Clear();

            foreach (var kv in _cancellations)
                kv.Value.Dispose();
            _cancellations.Clear();

            // 只有在 worker 线程确实已退出的情况下才敢清理 share/release 全局引用计数。
            // 否则可能触发 curl_global_cleanup 时 worker 仍在 libcurl 内部
            // （被用户回调卡住），与 global state 发生 use-after-free。
            // share handle 同理：若有 easy handle 仍附在 share 上 perform，
            // share_cleanup 会返回 CURLSHE_IN_USE / 与 native 状态竞争。
            // 与 Worker 里"跳过 multi cleanup"的策略保持一致：泄漏一次不会
            // 导致其它客户端故障；进程退出时 OS 会回收所有内存。
            if (_worker.WorkerExitedCleanly)
            {
                _cookieJar?.Dispose();
                CurlGlobal.Release(_api);
            }
            else
            {
                CurlLog.Error(
                    "CurlHttpClient.Dispose: worker did not exit cleanly; skipping cookie jar cleanup and CurlGlobal.Release to avoid " +
                    "curl_share_cleanup / curl_global_cleanup racing with the worker thread that is still inside libcurl.");
            }
        }

        private CurlRequest BuildCurlRequest(IHttpRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var curlReq = new CurlRequest(_api);
            try
            {
                ConfigureCurlRequest(curlReq, request);
                return curlReq;
            }
            catch
            {
                // 配置过程中抛了异常，释放已分配的 easy handle / slist / buffers，
                // 以免把半成品泄漏给调用方。
                curlReq.Dispose();
                throw;
            }
        }

        private void ConfigureCurlRequest(CurlRequest curlReq, IHttpRequest request)
        {
            var h = curlReq.Handle;

            // URL 是请求成立的前提；失败必须往外抛，不能偷偷走"URL 为空的 request"。
            CheckSetOpt("CURLOPT_URL", _api.SetOptString(h, CurlNative.CURLOPT_URL, request.Url));

            // Proxy：未设置时显式置空禁用 libcurl 读 HTTPS_PROXY/HTTP_PROXY 环境变量
            // （避免进程环境泄漏到网络配置）;设置后由 URL scheme 自动决定代理类型。
            // HTTP/3 无法经由 HTTP 代理,libcurl 会自动回退到 HTTP/2 over TCP。
            var proxy = _proxy; // 本次 build 内读一次,SetProxy/ClearProxy 只影响后续请求
            if (proxy == null)
            {
                CheckSetOpt("CURLOPT_PROXY", _api.SetOptString(h, CurlNative.CURLOPT_PROXY, ""));
            }
            else
            {
                CheckSetOpt("CURLOPT_PROXY", _api.SetOptString(h, CurlNative.CURLOPT_PROXY, proxy.Url));
                // 同时屏蔽 NO_PROXY 环境变量, 否则 target 是 loopback/内网时会被静默绕过代理
                CheckSetOpt("CURLOPT_NOPROXY", _api.SetOptString(h, CurlNative.CURLOPT_NOPROXY, ""));
                if (proxy.Credentials != null)
                {
                    var user = proxy.Credentials.UserName ?? string.Empty;
                    var pwd = proxy.Credentials.Password ?? string.Empty;
                    CheckSetOpt("CURLOPT_PROXYUSERPWD",
                        _api.SetOptString(h, CurlNative.CURLOPT_PROXYUSERPWD, $"{user}:{pwd}"));
                }
            }

            // 多线程环境必须禁用信号，避免 Unix 下 SIGALRM 干扰其他线程
            CheckSetOpt("CURLOPT_NOSIGNAL", _api.SetOptLong(h, CurlNative.CURLOPT_NOSIGNAL, 1));

            // HTTP version（枚举值与 curl 定义一致，直接 cast）
            CheckSetOpt("CURLOPT_HTTP_VERSION",
                _api.SetOptLong(h, CurlNative.CURLOPT_HTTP_VERSION, (long)PreferredVersion));

            // SSL 验证
            if (!VerifySSL)
            {
                CheckSetOpt("CURLOPT_SSL_VERIFYPEER",
                    _api.SetOptLong(h, CurlNative.CURLOPT_SSL_VERIFYPEER, 0));
                CheckSetOpt("CURLOPT_SSL_VERIFYHOST",
                    _api.SetOptLong(h, CurlNative.CURLOPT_SSL_VERIFYHOST, 0));
            }

            // Body / BodyStream 互斥 + 语义校验
            bool hasStream = request.BodyStream != null;
            bool hasBody = request.Body != null;  // 互斥判断不依赖长度, 空 byte[] 也禁止与 stream 共存
            if (hasStream && hasBody)
                throw new InvalidOperationException(
                    "HttpRequest.Body 与 BodyStream 互斥, 同时设置无法确定上传源");
            if (hasStream && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head))
                throw new InvalidOperationException(
                    $"HTTP {request.Method} 不允许带 body; BodyStream 需配合 POST/PUT/PATCH 等方法");
            if (hasStream && request.BodyLength.HasValue && request.BodyLength.Value < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(request.BodyLength), "BodyLength 不能为负数");

            // Method / Body
            if (hasStream)
            {
                // 流式:统一用 UPLOAD=1 + CUSTOMREQUEST 指定方法;UPLOAD 让 libcurl 调 READFUNCTION
                // 拉取 body。与 CURLOPT_POST=1 互斥,但 CUSTOMREQUEST 可覆盖成 POST。
                CheckSetOpt("CURLOPT_UPLOAD", _api.SetOptLong(h, CurlNative.CURLOPT_UPLOAD, 1));
                CheckSetOpt("CURLOPT_CUSTOMREQUEST",
                    _api.SetOptString(h, CurlNative.CURLOPT_CUSTOMREQUEST,
                        request.Method.ToString().ToUpperInvariant()));
                if (request.BodyLength.HasValue)
                {
                    // 已知长度: 写 Content-Length header, 按 fixed-length 上传
                    CheckSetOpt("CURLOPT_INFILESIZE_LARGE",
                        _api.SetOptOffT(h, CurlNative.CURLOPT_INFILESIZE_LARGE, request.BodyLength.Value));
                }
                // 未设 INFILESIZE → libcurl 默认走 Transfer-Encoding: chunked
                curlReq.UploadStream = request.BodyStream;
                // READFUNCTION 的注册由 CurlMulti.Send 根据 UploadStream != null 统一处理
            }
            else
            {
                switch (request.Method)
                {
                    case HttpMethod.Get:
                        break; // GET 是默认
                    case HttpMethod.Post:
                        CheckSetOpt("CURLOPT_POST", _api.SetOptLong(h, CurlNative.CURLOPT_POST, 1));
                        break;
                    case HttpMethod.Head:
                        CheckSetOpt("CURLOPT_NOBODY", _api.SetOptLong(h, CurlNative.CURLOPT_NOBODY, 1));
                        break;
                    default:
                        CheckSetOpt("CURLOPT_CUSTOMREQUEST",
                            _api.SetOptString(h, CurlNative.CURLOPT_CUSTOMREQUEST,
                                request.Method.ToString().ToUpperInvariant()));
                        break;
                }

                // byte[] Body: 先设 size 再设 data，COPYPOSTFIELDS 会复制内容。
                // 空 byte[] (Length == 0) 走默认, 不需要设 POSTFIELDS。
                if (hasBody && request.Body.Length > 0)
                {
                    CheckSetOpt("CURLOPT_POSTFIELDSIZE_LARGE",
                        _api.SetOptOffT(h, CurlNative.CURLOPT_POSTFIELDSIZE_LARGE, request.Body.Length));
                    var pin = System.Runtime.InteropServices.GCHandle.Alloc(request.Body,
                        System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        CheckSetOpt("CURLOPT_COPYPOSTFIELDS",
                            _api.SetOptPtr(h, CurlNative.CURLOPT_COPYPOSTFIELDS, pin.AddrOfPinnedObject()));
                    }
                    finally
                    {
                        pin.Free(); // curl 已复制数据，可以立即释放 pin
                    }
                }
            }

            // Headers: slist 生命周期由 CurlRequest.Dispose 管理
            if (request.Headers != null)
            {
                var slist = IntPtr.Zero;
                foreach (var kv in request.Headers)
                {
                    var next = _api.SListAppend(slist, $"{kv.Key}: {kv.Value}");
                    if (next == IntPtr.Zero)
                    {
                        // slist_append 返回 NULL 通常是 OOM。已积累的节点由 curlReq 最终 Dispose
                        // 时释放——我们先把当前 slist 挂上去，保证错误路径也能回收。
                        if (slist != IntPtr.Zero)
                            curlReq.HeaderSlist = slist;
                        throw new InvalidOperationException(
                            $"curl_slist_append returned null while building header for key '{kv.Key}'");
                    }
                    slist = next;
                }

                if (slist != IntPtr.Zero)
                {
                    curlReq.HeaderSlist = slist;
                    CheckSetOpt("CURLOPT_HTTPHEADER",
                        _api.SetOptPtr(h, CurlNative.CURLOPT_HTTPHEADER, slist));
                }
            }

            // Timeouts
            if (request.ConnectTimeoutMs > 0)
                CheckSetOpt("CURLOPT_CONNECTTIMEOUT_MS",
                    _api.SetOptLong(h, CurlNative.CURLOPT_CONNECTTIMEOUT_MS, request.ConnectTimeoutMs));
            if (request.TimeoutMs > 0)
                CheckSetOpt("CURLOPT_TIMEOUT_MS",
                    _api.SetOptLong(h, CurlNative.CURLOPT_TIMEOUT_MS, request.TimeoutMs));

            // Follow redirects
            CheckSetOpt("CURLOPT_FOLLOWLOCATION",
                _api.SetOptLong(h, CurlNative.CURLOPT_FOLLOWLOCATION, 1));

            // Cookies：挂到 client 共享 jar 上 + 激活 cookie engine。
            // 注意：这里不能用 CURLOPT_COOKIELIST=""（那是"清空 jar"指令，会擦掉其他
            // handle 刚写入的 cookie）。COOKIEFILE="" 只激活引擎、不读文件。
            if (request.EnableCookies)
            {
                EnsureCookieJar();
                CheckSetOpt("CURLOPT_SHARE",
                    _api.SetOptPtr(h, CurlNative.CURLOPT_SHARE, _cookieJar.Handle));
                CheckSetOpt("CURLOPT_COOKIEFILE",
                    _api.SetOptString(h, CurlNative.CURLOPT_COOKIEFILE, ""));
            }

            // Response headers capture
            curlReq.CaptureHeaders = request.EnableResponseHeaders;

            // Streaming
            curlReq.DataCallback = request.OnDataReceived;
        }

        private void EnsureCookieJar()
        {
            if (_cookieJar != null) return;
            // 允许并发首次请求；CompareExchange 输的一方本地构造的 jar 需被清理。
            var fresh = new CurlCookieJar(_api);
            if (Interlocked.CompareExchange(ref _cookieJar, fresh, null) != null)
                fresh.Dispose();
        }

        private void CheckSetOpt(string optName, int rc)
        {
            if (rc == CurlNative.CURLE_OK) return;
            throw new InvalidOperationException(
                $"curl_easy_setopt({optName}) failed (code {rc}): {_api.GetErrorString(rc)}");
        }
    }
}
