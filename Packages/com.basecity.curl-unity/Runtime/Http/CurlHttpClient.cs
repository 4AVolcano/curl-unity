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
    /// <b>代理行为：</b>本客户端默认不使用代理，并显式屏蔽 libcurl 对
    /// <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c> 等环境变量的自动读取，避免
    /// 开发/宿主机上的代理泄漏到业务网络配置，且 HTTP/3 本身无法经由 HTTP 代理。
    /// 显式代理支持作为独立特性后续提供（见项目 Roadmap 的 Proxy 任务）。
    /// </para>
    /// </remarks>
    public class CurlHttpClient : IHttpClient
    {
        private readonly CurlBackgroundWorker _worker;
        private readonly ConcurrentDictionary<IntPtr, CancellationTokenRegistration> _cancellations = new();
        private readonly ConcurrentDictionary<TaskCompletionSource<IHttpResponse>, byte> _pendingTasks = new();
        private bool _disposed;

        /// <summary>全局 HTTP 版本偏好。对所有后续请求生效。默认 PreferH3。</summary>
        public HttpVersion PreferredVersion { get; set; } = HttpVersion.PreferH3;

        /// <summary>是否验证 SSL 证书。默认 true。</summary>
        public bool VerifySSL { get; set; } = true;

        /// <summary>诊断统计。构造时 enableDiagnostics=true 才可用，否则为 null。</summary>
        public HttpDiagnostics Diagnostics { get; }

        public CurlHttpClient(bool enableDiagnostics = false)
        {
            CurlGlobal.Acquire();
            CurlCerts.Initialize();
            if (enableDiagnostics)
                Diagnostics = new HttpDiagnostics();
            _worker = new CurlBackgroundWorker();
            _worker.Start();
        }

        public Task<IHttpResponse> SendAsync(IHttpRequest request, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CurlHttpClient));

            var tcs = new TaskCompletionSource<IHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var curlReq = BuildCurlRequest(request);

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
                    var response = new HttpResponse(curlResp);
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
            if (_disposed) return;
            _disposed = true;
            _worker.Dispose();

            // Worker 已停止，所有未完成的 Task 设置异常
            var ex = new ObjectDisposedException(nameof(CurlHttpClient));
            foreach (var kv in _pendingTasks)
                kv.Key.TrySetException(ex);
            _pendingTasks.Clear();

            foreach (var kv in _cancellations)
                kv.Value.Dispose();
            _cancellations.Clear();

            CurlGlobal.Release();
        }

        private CurlRequest BuildCurlRequest(IHttpRequest request)
        {
            var curlReq = new CurlRequest();
            var h = curlReq.Handle;

            // URL
            CurlNative.curl_unity_setopt_string(h, CurlNative.CURLOPT_URL, request.Url);

            // 禁用 libcurl 默认读取 HTTPS_PROXY/HTTP_PROXY 环境变量的行为，避免
            // 进程环境泄漏到网络配置（且 HTTP/3 本身无法经由 HTTP 代理）。
            // 显式 Proxy 支持后续作为独立特性开放。
            CurlNative.curl_unity_setopt_string(h, CurlNative.CURLOPT_PROXY, "");

            // 多线程环境必须禁用信号，避免 Unix 下 SIGALRM 干扰其他线程
            CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_NOSIGNAL, 1);

            // HTTP version（枚举值与 curl 定义一致，直接 cast）
            CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_HTTP_VERSION, (long)PreferredVersion);

            // SSL 验证
            if (!VerifySSL)
            {
                CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_SSL_VERIFYPEER, 0);
                CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_SSL_VERIFYHOST, 0);
            }

            // Method
            switch (request.Method)
            {
                case HttpMethod.Get:
                    break; // GET 是默认
                case HttpMethod.Post:
                    CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_POST, 1);
                    break;
                case HttpMethod.Head:
                    CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_NOBODY, 1);
                    break;
                default:
                    CurlNative.curl_unity_setopt_string(h, CurlNative.CURLOPT_CUSTOMREQUEST,
                        request.Method.ToString().ToUpperInvariant());
                    break;
            }

            // Body: 先设 size 再设 data，COPYPOSTFIELDS 会复制内容
            if (request.Body != null && request.Body.Length > 0)
            {
                CurlNative.curl_unity_setopt_off_t(h, CurlNative.CURLOPT_POSTFIELDSIZE_LARGE, request.Body.Length);
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(request.Body,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    CurlNative.curl_unity_setopt_ptr(h, CurlNative.CURLOPT_COPYPOSTFIELDS, pin.AddrOfPinnedObject());
                }
                finally
                {
                    pin.Free(); // curl 已复制数据，可以立即释放 pin
                }
            }

            // Headers: slist 生命周期由 CurlRequest.Dispose 管理
            if (request.Headers != null)
            {
                var slist = IntPtr.Zero;
                foreach (var kv in request.Headers)
                    slist = CurlNative.curl_slist_append(slist, $"{kv.Key}: {kv.Value}");

                if (slist != IntPtr.Zero)
                {
                    curlReq.HeaderSlist = slist;
                    CurlNative.curl_unity_setopt_ptr(h, CurlNative.CURLOPT_HTTPHEADER, slist);
                }
            }

            // Timeouts
            if (request.ConnectTimeoutMs > 0)
                CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_CONNECTTIMEOUT_MS, request.ConnectTimeoutMs);
            if (request.TimeoutMs > 0)
                CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_TIMEOUT_MS, request.TimeoutMs);

            // Follow redirects
            CurlNative.curl_unity_setopt_long(h, CurlNative.CURLOPT_FOLLOWLOCATION, 1);

            // Cookies
            if (request.EnableCookies)
                CurlNative.curl_unity_setopt_string(h, CurlNative.CURLOPT_COOKIELIST, "");

            // Response headers capture
            curlReq.CaptureHeaders = request.EnableResponseHeaders;

            // Streaming
            curlReq.DataCallback = request.OnDataReceived;

            return curlReq;
        }
    }
}
