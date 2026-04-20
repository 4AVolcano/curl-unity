using System;
using CurlUnity.Native;

namespace CurlUnity.Core
{
    /// <summary>
    /// 引用计数式管理 curl_global_init / curl_global_cleanup。
    /// 第一个 CurlHttpClient 构造时 Acquire，最后一个 Dispose 时 Release。
    /// </summary>
    internal static class CurlGlobal
    {
        private static int _refCount;
        private static readonly object _lock = new();

        public static void Acquire()
        {
            Acquire(CurlNativeApi.Instance);
        }

        internal static void Acquire(ICurlApi api)
        {
            lock (_lock)
            {
                if (_refCount == 0)
                {
                    var rc = api.CurlGlobalInit(CurlNative.CURL_GLOBAL_DEFAULT);
                    if (rc != CurlNative.CURLE_OK)
                    {
                        // init 失败不递增引用计数，否则 Release 会调 cleanup 一个未初始化的库。
                        throw new InvalidOperationException(
                            $"curl_global_init failed (code {rc}): {api.GetErrorString(rc)}");
                    }
                }
                _refCount++;
            }
        }

        public static void Release()
        {
            Release(CurlNativeApi.Instance);
        }

        internal static void Release(ICurlApi api)
        {
            lock (_lock)
            {
                if (_refCount == 0) return; // 被过度 Release，保护性忽略
                if (--_refCount == 0)
                    api.CurlGlobalCleanup();
            }
        }
    }
}
