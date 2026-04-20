using System;
using System.Runtime.InteropServices;
using System.Threading;
#if UNITY_5_3_OR_NEWER
using AOT;
#endif
using CurlUnity.Native;

namespace CurlUnity.Core
{
    /// <summary>
    /// 基于 libcurl <c>CURLSH</c> 的 cookie jar。
    /// <para>
    /// 一个 <see cref="CurlUnity.Http.CurlHttpClient"/> 持有一个 jar；client 下所有
    /// <c>EnableCookies=true</c> 的请求都挂到这个 share 上，实现跨请求 cookie 共享。
    /// 不同 client 之间互相独立（各自持有自己的 jar）。
    /// </para>
    /// <para>
    /// <b>锁策略：</b>libcurl 的 unlock 回调不传 <c>curl_lock_access</c>，
    /// 无法区分释放的是读锁还是写锁，所以这里退化成单把 <see cref="Monitor"/>
    /// 互斥锁。本项目的 easy handle 由单线程 worker 驱动，不会真正产生竞争；
    /// 但 <c>CURLOPT_SHARE</c> 在 <c>SendAsync</c> 调用线程上设置，为健壮性
    /// 仍然注册实锁回调。
    /// </para>
    /// </summary>
    internal sealed class CurlCookieJar : IDisposable
    {
        // delegate 实例必须静态持有，避免 GC 回收导致 libcurl 持有悬挂函数指针。
        private static readonly CurlNative.ShareLockCallback s_lockCb = LockCallback;
        private static readonly CurlNative.ShareUnlockCallback s_unlockCb = UnlockCallback;

        private readonly ICurlApi _api;
        private readonly object _cookieLock = new();
        private IntPtr _handle;
        private GCHandle _selfHandle;
        private int _disposedFlag;

        public IntPtr Handle => _handle;

        public CurlCookieJar(ICurlApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));

            _handle = _api.ShareInit();
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("curl_share_init returned NULL");

            _selfHandle = GCHandle.Alloc(this);

            try
            {
                Check("CURLSHOPT_SHARE",
                    _api.ShareSetOptLong(_handle, CurlNative.CURLSHOPT_SHARE,
                        CurlNative.CURL_LOCK_DATA_COOKIE));

                Check("CURLSHOPT_LOCKFUNC",
                    _api.ShareSetOptPtr(_handle, CurlNative.CURLSHOPT_LOCKFUNC,
                        Marshal.GetFunctionPointerForDelegate(s_lockCb)));

                Check("CURLSHOPT_UNLOCKFUNC",
                    _api.ShareSetOptPtr(_handle, CurlNative.CURLSHOPT_UNLOCKFUNC,
                        Marshal.GetFunctionPointerForDelegate(s_unlockCb)));

                Check("CURLSHOPT_USERDATA",
                    _api.ShareSetOptPtr(_handle, CurlNative.CURLSHOPT_USERDATA,
                        GCHandle.ToIntPtr(_selfHandle)));
            }
            catch
            {
                _api.ShareCleanup(_handle);
                _handle = IntPtr.Zero;
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

            if (_handle != IntPtr.Zero)
            {
                var rc = _api.ShareCleanup(_handle);
                if (rc != CurlNative.CURLSHE_OK)
                {
                    CurlLog.Error(
                        $"curl_share_cleanup failed (code {rc}): {_api.GetShareErrorString(rc)}. " +
                        "This usually means an easy handle was still associated with the share — check CurlHttpClient.Dispose ordering.");
                }
                _handle = IntPtr.Zero;
            }

            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }

        private void Check(string name, int rc)
        {
            if (rc == CurlNative.CURLSHE_OK) return;
            throw new InvalidOperationException(
                $"curl_share_setopt({name}) failed (code {rc}): {_api.GetShareErrorString(rc)}");
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.ShareLockCallback))]
#endif
        private static void LockCallback(IntPtr handle, int data, int access, IntPtr userdata)
        {
            // 回调跨 native 边界，异常必须吞掉，否则 libcurl 拿到未定义状态。
            try
            {
                var self = (CurlCookieJar)GCHandle.FromIntPtr(userdata).Target;
                Monitor.Enter(self._cookieLock);
            }
            catch (Exception ex)
            {
                CurlLog.Error($"CurlCookieJar.LockCallback threw: {ex}");
            }
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.ShareUnlockCallback))]
#endif
        private static void UnlockCallback(IntPtr handle, int data, IntPtr userdata)
        {
            try
            {
                var self = (CurlCookieJar)GCHandle.FromIntPtr(userdata).Target;
                Monitor.Exit(self._cookieLock);
            }
            catch (Exception ex)
            {
                CurlLog.Error($"CurlCookieJar.UnlockCallback threw: {ex}");
            }
        }
    }
}
