using CurlUnity.Diagnostics;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    public class HttpDiagnosticsSnapshotTests
    {
        /// <summary>
        /// ConnectionReuseRate = 1 - uniqueConnections/totalRequests, 当 totalRequests=0
        /// 要返回 0 而不是除零 NaN。这个边界容易被 refactor 破坏。
        /// </summary>
        [Fact]
        public void ConnectionReuseRate_ZeroRequests_ReturnsZeroNotNaN()
        {
            var snap = new HttpDiagnosticsSnapshot(
                totalRequests: 0,
                successRequests: 0,
                failedRequests: 0,
                uniqueConnections: 0,
                totalDownloadBytes: 0,
                totalUploadBytes: 0,
                avgDnsTimeUs: 0,
                avgConnectTimeUs: 0,
                avgTlsTimeUs: 0,
                avgFirstByteTimeUs: 0,
                avgTotalTimeUs: 0);

            Assert.Equal(0.0, snap.ConnectionReuseRate);
            Assert.False(double.IsNaN(snap.ConnectionReuseRate));
        }

        /// <summary>uniqueConnections=0 也该是 0(未做过任何连接统计, 复用率无意义)。</summary>
        [Fact]
        public void ConnectionReuseRate_ZeroUniqueConnections_ReturnsZero()
        {
            var snap = new HttpDiagnosticsSnapshot(
                totalRequests: 5,
                successRequests: 5, failedRequests: 0, uniqueConnections: 0,
                totalDownloadBytes: 0, totalUploadBytes: 0,
                avgDnsTimeUs: 0, avgConnectTimeUs: 0, avgTlsTimeUs: 0,
                avgFirstByteTimeUs: 0, avgTotalTimeUs: 0);

            Assert.Equal(0.0, snap.ConnectionReuseRate);
        }

        /// <summary>10 个请求复用到 3 个连接, 复用率 1 - 3/10 = 0.7。</summary>
        [Fact]
        public void ConnectionReuseRate_WithReuse_CalculatesCorrectly()
        {
            var snap = new HttpDiagnosticsSnapshot(
                totalRequests: 10,
                successRequests: 10, failedRequests: 0, uniqueConnections: 3,
                totalDownloadBytes: 0, totalUploadBytes: 0,
                avgDnsTimeUs: 0, avgConnectTimeUs: 0, avgTlsTimeUs: 0,
                avgFirstByteTimeUs: 0, avgTotalTimeUs: 0);

            Assert.Equal(0.7, snap.ConnectionReuseRate, precision: 10);
        }

        /// <summary>
        /// 失败请求不产生连接，复用率分母必须用成功数：10 总 / 5 成功 / 3 连接
        /// → 1 - 3/5 = 0.4（用总数会虚高到 0.7）。
        /// </summary>
        [Fact]
        public void ConnectionReuseRate_WithFailures_UsesSuccessDenominator()
        {
            var snap = new HttpDiagnosticsSnapshot(
                totalRequests: 10,
                successRequests: 5, failedRequests: 5, uniqueConnections: 3,
                totalDownloadBytes: 0, totalUploadBytes: 0,
                avgDnsTimeUs: 0, avgConnectTimeUs: 0, avgTlsTimeUs: 0,
                avgFirstByteTimeUs: 0, avgTotalTimeUs: 0);

            Assert.Equal(0.4, snap.ConnectionReuseRate, precision: 10);
        }
    }
}
