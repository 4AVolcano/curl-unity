using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CurlUnity.Http;
using CurlUnity.Native;

namespace CurlUnity.Diagnostics
{
    public class HttpDiagnostics
    {
        private const int PruneThreshold = 100;

        private readonly object _lock = new();
        private readonly HashSet<long> _connIds = new();
        private readonly ConcurrentDictionary<IHttpResponse, HttpRequestTiming> _timings = new();
        private int _totalRequests;
        private int _successRequests;
        private int _failedRequests;
        private long _totalDownloadBytes;
        private long _totalUploadBytes;
        private long _sumDnsTimeUs;
        private long _sumConnectTimeUs;
        private long _sumTlsTimeUs;
        private long _sumFirstByteTimeUs;
        private long _sumTotalTimeUs;

        public HttpRequestTiming GetTiming(IHttpResponse response)
        {
            return _timings.TryGetValue(response, out var timing) ? timing : default;
        }

        public HttpDiagnosticsSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                var total = _totalRequests;
                return new HttpDiagnosticsSnapshot(
                    totalRequests: total,
                    successRequests: _successRequests,
                    failedRequests: _failedRequests,
                    uniqueConnections: _connIds.Count,
                    totalDownloadBytes: _totalDownloadBytes,
                    totalUploadBytes: _totalUploadBytes,
                    avgDnsTimeUs: total > 0 ? _sumDnsTimeUs / total : 0,
                    avgConnectTimeUs: total > 0 ? _sumConnectTimeUs / total : 0,
                    avgTlsTimeUs: total > 0 ? _sumTlsTimeUs / total : 0,
                    avgFirstByteTimeUs: total > 0 ? _sumFirstByteTimeUs / total : 0,
                    avgTotalTimeUs: total > 0 ? _sumTotalTimeUs / total : 0
                );
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _connIds.Clear();
                _timings.Clear();
                _totalRequests = 0;
                _successRequests = 0;
                _failedRequests = 0;
                _totalDownloadBytes = 0;
                _totalUploadBytes = 0;
                _sumDnsTimeUs = 0;
                _sumConnectTimeUs = 0;
                _sumTlsTimeUs = 0;
                _sumFirstByteTimeUs = 0;
                _sumTotalTimeUs = 0;
            }
        }

        internal void Record(HttpResponse response)
        {
            var timing = ReadTiming(response.EasyHandle);
            _timings[response] = timing;

            if (_timings.Count > PruneThreshold)
                Prune();

            lock (_lock)
            {
                _totalRequests++;
                if (response.HasResponse) _successRequests++; else _failedRequests++;

                if (timing.ConnectionId >= 0)
                    _connIds.Add(timing.ConnectionId);

                _totalDownloadBytes += timing.DownloadBytes;
                _totalUploadBytes += timing.UploadBytes;
                _sumDnsTimeUs += timing.DnsTimeUs;
                _sumConnectTimeUs += timing.ConnectTimeUs;
                _sumTlsTimeUs += timing.TlsTimeUs;
                _sumFirstByteTimeUs += timing.FirstByteTimeUs;
                _sumTotalTimeUs += timing.TotalTimeUs;
            }
        }

        private void Prune()
        {
            foreach (var kv in _timings)
            {
                if (kv.Key.IsDisposed)
                    _timings.TryRemove(kv.Key, out _);
            }
        }

        private static HttpRequestTiming ReadTiming(IntPtr easyHandle)
        {
            if (easyHandle == IntPtr.Zero)
                return default;

            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_NAMELOOKUP_TIME_T, out var dns);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_CONNECT_TIME_T, out var connect);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_APPCONNECT_TIME_T, out var appConnect);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_STARTTRANSFER_TIME_T, out var firstByte);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_TOTAL_TIME_T, out var total);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_REDIRECT_TIME_T, out var redirect);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_SIZE_DOWNLOAD_T, out var dlBytes);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_SIZE_UPLOAD_T, out var ulBytes);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_SPEED_DOWNLOAD_T, out var dlSpeed);
            CurlNative.curl_unity_getinfo_long(easyHandle, CurlNative.CURLINFO_NUM_CONNECTS, out var numConnects);
            CurlNative.curl_unity_getinfo_off_t(easyHandle, CurlNative.CURLINFO_CONN_ID, out var connId);

            return new HttpRequestTiming(
                dnsTimeUs: dns,
                connectTimeUs: connect,
                tlsTimeUs: appConnect > connect ? appConnect - connect : 0,
                firstByteTimeUs: firstByte,
                totalTimeUs: total,
                redirectTimeUs: redirect,
                downloadBytes: dlBytes,
                uploadBytes: ulBytes,
                downloadSpeedBps: dlSpeed,
                newConnections: (int)numConnects,
                connectionId: connId
            );
        }
    }
}
