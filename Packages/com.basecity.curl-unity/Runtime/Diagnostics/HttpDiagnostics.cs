using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CurlUnity.Http;
using CurlUnity.Native;

namespace CurlUnity.Diagnostics
{
    /// <summary>
    /// 累积 <see cref="IHttpClient"/> 的请求级时序与连接统计(DNS / TCP / TLS /
    /// TTFB / 总耗时 / 连接复用)。在 <see cref="CurlHttpClient"/> 构造时通过
    /// <c>enableDiagnostics: true</c> 开启;未开启时 <see cref="CurlHttpClient.Diagnostics"/>
    /// 为 null。
    /// </summary>
    /// <remarks>
    /// 内部用字典保留最近请求的 per-request <see cref="HttpRequestTiming"/>,通过
    /// <see cref="GetTiming"/> 可查 (前提是调用方还持有对应的 <see cref="IHttpResponse"/>
    /// 引用)。统计条目会在容量超过阈值时清理已 Dispose 的响应。
    /// </remarks>
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

        /// <summary>
        /// 按 <paramref name="response"/> 查询该请求的 <see cref="HttpRequestTiming"/>。
        /// 响应已被清理(或非本 client 产出)时返回 default 值(全 0)。
        /// </summary>
        public HttpRequestTiming GetTiming(IHttpResponse response)
        {
            return _timings.TryGetValue(response, out var timing) ? timing : default;
        }

        /// <summary>
        /// 导出聚合指标快照(平均耗时、累计字节数、连接复用率等)。快照一次性计算,
        /// 不随后续请求变化。
        /// </summary>
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

        /// <summary>清空所有累积的统计与 per-request timing。常用于测试或分阶段采样。</summary>
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

        /// <summary>
        /// 成功路径: 记录一次拿到 HTTP 响应的请求(含 4xx/5xx,它们不算失败)。由
        /// <see cref="CurlHttpClient"/> 在构造 response 后调用。
        /// </summary>
        internal void Record(HttpResponse response)
        {
            var timing = ReadTiming(response);
            _timings[response] = timing;

            if (_timings.Count > PruneThreshold)
                Prune();

            lock (_lock)
            {
                _totalRequests++;
                _successRequests++;

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

        /// <summary>
        /// 失败路径: 记录一次在网络/协议层失败的请求(<see cref="CurlHttpException"/> 或
        /// 用户回调 rethrow)。不计入 timing 和字节数(没有可靠数据)。由
        /// <see cref="CurlHttpClient"/> 在抛异常前调用。取消和 build 阶段的用法错误不算。
        /// </summary>
        internal void RecordFailure()
        {
            lock (_lock)
            {
                _totalRequests++;
                _failedRequests++;
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

        private static HttpRequestTiming ReadTiming(HttpResponse response)
        {
            if (response == null || response.IsDisposed)
                return default;

            response.TryGetInfoOffT(CurlNative.CURLINFO_NAMELOOKUP_TIME_T, out var dns);
            response.TryGetInfoOffT(CurlNative.CURLINFO_CONNECT_TIME_T, out var connect);
            response.TryGetInfoOffT(CurlNative.CURLINFO_APPCONNECT_TIME_T, out var appConnect);
            response.TryGetInfoOffT(CurlNative.CURLINFO_STARTTRANSFER_TIME_T, out var firstByte);
            response.TryGetInfoOffT(CurlNative.CURLINFO_TOTAL_TIME_T, out var total);
            response.TryGetInfoOffT(CurlNative.CURLINFO_REDIRECT_TIME_T, out var redirect);
            response.TryGetInfoOffT(CurlNative.CURLINFO_SIZE_DOWNLOAD_T, out var dlBytes);
            response.TryGetInfoOffT(CurlNative.CURLINFO_SIZE_UPLOAD_T, out var ulBytes);
            response.TryGetInfoOffT(CurlNative.CURLINFO_SPEED_DOWNLOAD_T, out var dlSpeed);
            response.TryGetInfoLong(CurlNative.CURLINFO_NUM_CONNECTS, out var numConnects);
            response.TryGetInfoOffT(CurlNative.CURLINFO_CONN_ID, out var connId);

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
