using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CurlUnity.Core;
using CurlUnity.Native;

namespace CurlUnity.Http
{
    internal class HttpResponse : IHttpResponse
    {
        private readonly ICurlApi _api;
        private IntPtr _easyHandle;
        private readonly int _curlCode;
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
            _curlCode = raw.CurlCode;
            _statusCode = raw.StatusCode;
            _body = raw.Body;
            _rawHeaders = raw.RawHeaders;
        }

        internal IntPtr EasyHandle => _easyHandle;

        public bool IsDisposed => _easyHandle == IntPtr.Zero;
        public bool HasResponse => _curlCode == CurlNative.CURLE_OK;
        public int StatusCode => (int)_statusCode;
        public int ErrorCode => _curlCode;

        public string ErrorMessage
        {
            get
            {
                if (_curlCode == CurlNative.CURLE_OK) return null;
                return _api.GetErrorString(_curlCode);
            }
        }

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
                if (!TryGetInfoString(CurlNative.CURLINFO_CONTENT_TYPE, out var ptr)) return null;
                return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : null;
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
                if (!TryGetInfoString(CurlNative.CURLINFO_EFFECTIVE_URL, out var ptr)) return null;
                return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : null;
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
            // Interlocked 保证并发 Dispose 只有一次真正执行 EasyCleanup，
            // 避免 double-free。
            var handle = Interlocked.Exchange(ref _easyHandle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                _api.EasyCleanup(handle);
        }

        internal bool TryGetInfoLong(int info, out long value)
        {
            value = 0;
            if (_easyHandle == IntPtr.Zero) return false;
            return _api.GetInfoLong(_easyHandle, info, out value) == CurlNative.CURLE_OK;
        }

        internal bool TryGetInfoString(int info, out IntPtr value)
        {
            value = IntPtr.Zero;
            if (_easyHandle == IntPtr.Zero) return false;
            return _api.GetInfoString(_easyHandle, info, out value) == CurlNative.CURLE_OK;
        }

        internal bool TryGetInfoOffT(int info, out long value)
        {
            value = 0;
            if (_easyHandle == IntPtr.Zero) return false;
            return _api.GetInfoOffT(_easyHandle, info, out value) == CurlNative.CURLE_OK;
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
