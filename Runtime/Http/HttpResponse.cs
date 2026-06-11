using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CurlUnity.Core;
using CurlUnity.Native;

namespace CurlUnity.Http
{
    internal class HttpResponse : IHttpResponse
    {
        private readonly ICurlApi _api;
        // 串行化 getinfo 与 Dispose/finalizer：惰性属性可能在任意线程被读，
        // 不加锁的话「检查非零 → 并发 cleanup → native 拿到已释放 handle」
        // 就是 use-after-free。getinfo 是纯内存读，锁开销可忽略。
        private readonly object _handleLock = new();
        private IntPtr _easyHandle;
        private readonly long _statusCode;
        private readonly byte[] _body;
        private readonly byte[] _rawHeaders;
        private IReadOnlyDictionary<string, string[]> _parsedHeaders;

        internal HttpResponse(CurlResponse raw)
            : this(CurlNativeApi.Instance, raw)
        {
        }

        internal HttpResponse(ICurlApi api, CurlResponse raw)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _easyHandle = raw.EasyHandle;
            _statusCode = raw.StatusCode;
            _body = raw.Body;
            _rawHeaders = raw.RawHeaders;

            // 持有 native easy handle 期间引用计数压住 curl_global_cleanup：
            // 调用方漏 Dispose 时 finalizer 才能安全地调 curl_easy_cleanup
            // （否则 client 全部 Dispose 后 global cleanup 先行，finalizer 再
            // cleanup 就是对已卸载库状态的 UAF）。
            if (_easyHandle != IntPtr.Zero)
                CurlGlobal.Acquire(_api);
            else
                GC.SuppressFinalize(this);
        }

        internal IntPtr EasyHandle => _easyHandle;

        public bool IsDisposed => _easyHandle == IntPtr.Zero;
        public int StatusCode => (int)_statusCode;

        public HttpVersion Version
        {
            get
            {
                if (!TryGetInfoLong(CurlNative.CURLINFO_HTTP_VERSION, out var v)) return HttpVersion.Default;
                return (HttpVersion)(int)v;
            }
        }

        public byte[] Body => _body;

        // --- 惰性 getinfo 属性，每次直接读 ---

        public string ContentType
        {
            get
            {
                return TryGetInfoString(CurlNative.CURLINFO_CONTENT_TYPE, out var s) ? s : null;
            }
        }

        public long ContentLength
        {
            get
            {
                if (!TryGetInfoOffT(CurlNative.CURLINFO_CONTENT_LENGTH_DOWNLOAD_T, out var v)) return -1;
                return v;
            }
        }

        public string EffectiveUrl
        {
            get
            {
                return TryGetInfoString(CurlNative.CURLINFO_EFFECTIVE_URL, out var s) ? s : null;
            }
        }

        public int RedirectCount
        {
            get
            {
                if (!TryGetInfoLong(CurlNative.CURLINFO_REDIRECT_COUNT, out var v)) return 0;
                return (int)v;
            }
        }

        public IReadOnlyDictionary<string, string[]> Headers
        {
            get
            {
                if (_rawHeaders == null) return null;
                return _parsedHeaders ??= ParseHeaders(_rawHeaders);
            }
        }

        public void Dispose()
        {
            ReleaseHandle(fromFinalizer: false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 泄漏安全网：调用方漏 Dispose 时由 GC 兜底回收 native easy handle，
        /// 记一条 warning 帮助定位泄漏点。正确用法仍是显式 Dispose/using——
        /// finalizer 触发时机不可控，期间 handle 及其缓冲一直占着 native 内存。
        /// </summary>
        ~HttpResponse()
        {
            ReleaseHandle(fromFinalizer: true);
        }

        private void ReleaseHandle(bool fromFinalizer)
        {
            // _handleLock 同时保证：
            //   1) 并发 Dispose / Dispose+finalizer 只有一次真正执行 EasyCleanup
            //   2) 与 TryGetInfo* 互斥——否则 getinfo 检查通过后 handle 被并发
            //      cleanup，native 层拿到已释放的 handle 就是 use-after-free
            IntPtr handle;
            lock (_handleLock)
            {
                handle = _easyHandle;
                _easyHandle = IntPtr.Zero;
            }
            if (handle == IntPtr.Zero) return;

            if (fromFinalizer)
                CurlLog.Warn(
                    "HttpResponse was garbage-collected without Dispose(); " +
                    "the native easy handle was reclaimed by the finalizer. " +
                    "Dispose responses explicitly (e.g. with `using`) to avoid native memory pressure.");

            _api.EasyCleanup(handle);
            CurlGlobal.Release(_api);
        }

        internal bool TryGetInfoLong(int info, out long value)
        {
            value = 0;
            lock (_handleLock)
            {
                if (_easyHandle == IntPtr.Zero) return false;
                return _api.GetInfoLong(_easyHandle, info, out value) == CurlNative.CURLE_OK;
            }
        }

        internal bool TryGetInfoString(int info, out string value)
        {
            value = null;
            // 返回的 char* 归 easy handle 所有，PtrToStringAnsi 必须也在锁内完成，
            // 否则指针在锁外解引用时 handle 可能已被并发 Dispose 释放。
            lock (_handleLock)
            {
                if (_easyHandle == IntPtr.Zero) return false;
                if (_api.GetInfoString(_easyHandle, info, out var ptr) != CurlNative.CURLE_OK)
                    return false;
                value = ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : null;
                return true;
            }
        }

        internal bool TryGetInfoOffT(int info, out long value)
        {
            value = 0;
            lock (_handleLock)
            {
                if (_easyHandle == IntPtr.Zero) return false;
                return _api.GetInfoOffT(_easyHandle, info, out value) == CurlNative.CURLE_OK;
            }
        }

        private static IReadOnlyDictionary<string, string[]> ParseHeaders(byte[] raw)
        {
            var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var text = Encoding.UTF8.GetString(raw);

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0 || trimmed.StartsWith("HTTP/"))
                    continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx <= 0) continue;

                var name = trimmed.Substring(0, colonIdx).Trim().ToLowerInvariant();
                var value = trimmed.Substring(colonIdx + 1).Trim();

                if (!dict.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    dict[name] = list;
                }
                list.Add(value);
            }

            var result = new Dictionary<string, string[]>(dict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in dict)
                result[kv.Key] = kv.Value.ToArray();
            return result;
        }
    }
}
