namespace CurlUnity.Diagnostics
{
    public readonly struct HttpDiagnosticsSnapshot
    {
        public readonly int TotalRequests;
        public readonly int SuccessRequests;
        public readonly int FailedRequests;
        public readonly int UniqueConnections;
        public readonly double ConnectionReuseRate;
        public readonly long TotalDownloadBytes;
        public readonly long TotalUploadBytes;
        public readonly long AvgDnsTimeUs;
        public readonly long AvgConnectTimeUs;
        public readonly long AvgTlsTimeUs;
        public readonly long AvgFirstByteTimeUs;
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
