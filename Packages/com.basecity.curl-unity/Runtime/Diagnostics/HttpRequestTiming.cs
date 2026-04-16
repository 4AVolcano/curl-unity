namespace CurlUnity.Diagnostics
{
    public readonly struct HttpRequestTiming
    {
        /// <summary>DNS 解析耗时 (μs)</summary>
        public readonly long DnsTimeUs;

        /// <summary>TCP 连接耗时 (μs，含 DNS)</summary>
        public readonly long ConnectTimeUs;

        /// <summary>TLS 握手耗时 (μs，= AppConnect - Connect)</summary>
        public readonly long TlsTimeUs;

        /// <summary>首字节到达 TTFB (μs)</summary>
        public readonly long FirstByteTimeUs;

        /// <summary>总耗时 (μs)</summary>
        public readonly long TotalTimeUs;

        /// <summary>重定向耗时 (μs)</summary>
        public readonly long RedirectTimeUs;

        /// <summary>下载字节数</summary>
        public readonly long DownloadBytes;

        /// <summary>上传字节数</summary>
        public readonly long UploadBytes;

        /// <summary>平均下载速度 (bytes/sec)</summary>
        public readonly long DownloadSpeedBps;

        /// <summary>新建连接数 (0 = 复用了已有连接)</summary>
        public readonly int NewConnections;

        /// <summary>连接 ID (同 ID = 同一连接，用于追踪连接复用)</summary>
        public readonly long ConnectionId;

        public HttpRequestTiming(long dnsTimeUs, long connectTimeUs, long tlsTimeUs,
            long firstByteTimeUs, long totalTimeUs, long redirectTimeUs,
            long downloadBytes, long uploadBytes, long downloadSpeedBps,
            int newConnections, long connectionId)
        {
            DnsTimeUs = dnsTimeUs;
            ConnectTimeUs = connectTimeUs;
            TlsTimeUs = tlsTimeUs;
            FirstByteTimeUs = firstByteTimeUs;
            TotalTimeUs = totalTimeUs;
            RedirectTimeUs = redirectTimeUs;
            DownloadBytes = downloadBytes;
            UploadBytes = uploadBytes;
            DownloadSpeedBps = downloadSpeedBps;
            NewConnections = newConnections;
            ConnectionId = connectionId;
        }

        public override string ToString() =>
            $"DNS={DnsTimeUs}μs Connect={ConnectTimeUs}μs TLS={TlsTimeUs}μs " +
            $"TTFB={FirstByteTimeUs}μs Total={TotalTimeUs}μs " +
            $"Down={DownloadBytes}B@{DownloadSpeedBps}B/s " +
            $"ConnID={ConnectionId} NewConn={NewConnections}";
    }
}
