using CurlUnity.Http;

namespace CurlUnity.Diagnostics
{
    /// <summary>
    /// <see cref="HttpDiagnostics"/> 聚合指标的一次性快照。字段都是快照时刻的累计值,
    /// 后续请求不影响快照。包含 <see cref="ToString"/> 便于日志直接输出。
    /// </summary>
    public readonly struct HttpDiagnosticsSnapshot
    {
        /// <summary>统计窗口内的总请求数(含失败)。取消和 build 前置用法错误不计入。</summary>
        public readonly int TotalRequests;
        /// <summary>收到完整 HTTP 响应的请求数(含 4xx/5xx)。</summary>
        public readonly int SuccessRequests;
        /// <summary>
        /// 以 <see cref="CurlHttpException"/> 或用户回调异常失败的请求数。
        /// 取消(<see cref="System.OperationCanceledException"/>)不计入。
        /// </summary>
        public readonly int FailedRequests;
        /// <summary>出现过的不同 libcurl 连接 ID 数量。越小说明连接复用越好。</summary>
        public readonly int UniqueConnections;
        /// <summary>连接复用率 = 1 - UniqueConnections / TotalRequests。无请求时为 0。</summary>
        public readonly double ConnectionReuseRate;
        /// <summary>累计下载字节数。</summary>
        public readonly long TotalDownloadBytes;
        /// <summary>累计上传字节数。</summary>
        public readonly long TotalUploadBytes;
        /// <summary>平均 DNS 解析耗时 (μs)。</summary>
        public readonly long AvgDnsTimeUs;
        /// <summary>平均 TCP 建连耗时 (μs,含 DNS)。</summary>
        public readonly long AvgConnectTimeUs;
        /// <summary>平均 TLS 握手耗时 (μs,= AppConnect - Connect)。</summary>
        public readonly long AvgTlsTimeUs;
        /// <summary>平均首字节到达 TTFB (μs)。</summary>
        public readonly long AvgFirstByteTimeUs;
        /// <summary>平均请求总耗时 (μs)。</summary>
        public readonly long AvgTotalTimeUs;

        public HttpDiagnosticsSnapshot(int totalRequests, int successRequests, int failedRequests,
            int uniqueConnections, long totalDownloadBytes, long totalUploadBytes,
            long avgDnsTimeUs, long avgConnectTimeUs, long avgTlsTimeUs,
            long avgFirstByteTimeUs, long avgTotalTimeUs)
        {
            TotalRequests = totalRequests;
            SuccessRequests = successRequests;
            FailedRequests = failedRequests;
            UniqueConnections = uniqueConnections;
            ConnectionReuseRate = totalRequests > 0 && uniqueConnections > 0
                ? 1.0 - (double)uniqueConnections / totalRequests
                : 0;
            TotalDownloadBytes = totalDownloadBytes;
            TotalUploadBytes = totalUploadBytes;
            AvgDnsTimeUs = avgDnsTimeUs;
            AvgConnectTimeUs = avgConnectTimeUs;
            AvgTlsTimeUs = avgTlsTimeUs;
            AvgFirstByteTimeUs = avgFirstByteTimeUs;
            AvgTotalTimeUs = avgTotalTimeUs;
        }

        public override string ToString() =>
            $"Requests={TotalRequests} (ok={SuccessRequests} fail={FailedRequests}) " +
            $"Connections={UniqueConnections} Reuse={ConnectionReuseRate:P0}\n" +
            $"AvgDNS={AvgDnsTimeUs}μs AvgConnect={AvgConnectTimeUs}μs AvgTLS={AvgTlsTimeUs}μs " +
            $"AvgTTFB={AvgFirstByteTimeUs}μs AvgTotal={AvgTotalTimeUs}μs\n" +
            $"TotalDown={TotalDownloadBytes}B TotalUp={TotalUploadBytes}B";
    }
}
