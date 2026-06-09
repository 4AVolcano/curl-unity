using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CurlUnity.Diagnostics;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class DiagnosticsTests : IDisposable
    {
        private readonly TestServerFixture _server;

        public DiagnosticsTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
        }

        public void Dispose() { }

        // 轮询诊断快照直到满足 until(或超时)。用于失败计数这类"最终一致"的统计:
        // 失败路径的 RecordFailure 在 worker 线程执行,与调用方 await 恢复
        // (tcs 是 RunContinuationsAsynchronously)并发,直接读可能抢在记录之前。
        // 命中 until 即返回;到超时仍未命中则返回最后一次快照,让断言据实失败
        // (保留发现"根本没记录"这类真问题的能力)。
        private static async Task<HttpDiagnosticsSnapshot> WaitForSnapshotAsync(
            HttpDiagnostics diagnostics,
            Func<HttpDiagnosticsSnapshot, bool> until,
            int timeoutMs = 2000)
        {
            var snapshot = diagnostics.GetSnapshot();
            for (int waited = 0; !until(snapshot) && waited < timeoutMs; waited += 10)
            {
                await Task.Delay(10);
                snapshot = diagnostics.GetSnapshot();
            }
            return snapshot;
        }

        [Fact]
        public async Task Diagnostics_RecordsTimingData()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            using var resp = await client.GetAsync($"{_server.HttpUrl}/hello");

            var timing = client.Diagnostics.GetTiming(resp);
            Assert.True(timing.TotalTimeUs > 0, $"TotalTimeUs should be > 0, got {timing.TotalTimeUs}");
        }

        [Fact]
        public async Task Diagnostics_Snapshot_AggregatesCorrectly()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            using var resp1 = await client.GetAsync($"{_server.HttpUrl}/hello");
            using var resp2 = await client.GetAsync($"{_server.HttpUrl}/json");

            var snapshot = client.Diagnostics.GetSnapshot();
            Assert.Equal(2, snapshot.TotalRequests);
            Assert.Equal(2, snapshot.SuccessRequests);
            Assert.Equal(0, snapshot.FailedRequests);
            Assert.True(snapshot.AvgTotalTimeUs > 0);
        }

        [Fact]
        public async Task Diagnostics_FailedRequest_CountedCorrectly()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            await Assert.ThrowsAsync<CurlHttpException>(
                () => client.GetAsync("http://localhost:1/nope"));

            // 失败计数最终一致(见 WaitForSnapshotAsync):轮询到记录落定再断言,
            // 消除 RecordFailure 与 await 恢复并发导致的偶发漏读。
            var snapshot = await WaitForSnapshotAsync(
                client.Diagnostics, s => s.TotalRequests >= 1);
            Assert.Equal(1, snapshot.TotalRequests);
            Assert.Equal(0, snapshot.SuccessRequests);
            Assert.Equal(1, snapshot.FailedRequests);
        }

        [Fact]
        public async Task ConcurrentRequests_AllSucceed()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            var tasks = Enumerable.Range(0, 10)
                .Select(_ => client.GetAsync($"{_server.HttpUrl}/hello"))
                .ToArray();

            var responses = await Task.WhenAll(tasks);

            try
            {
                foreach (var resp in responses)
                {
                    Assert.Equal(200, resp.StatusCode);
                }

                var snapshot = client.Diagnostics.GetSnapshot();
                Assert.Equal(10, snapshot.TotalRequests);
                Assert.Equal(10, snapshot.SuccessRequests);
            }
            finally
            {
                foreach (var resp in responses)
                    resp.Dispose();
            }
        }

        [Fact]
        public async Task Timing_AllFieldsPopulated_ForHttpRequest()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            using var resp = await client.GetAsync($"{_server.HttpUrl}/bytes/1000");

            var t = client.Diagnostics.GetTiming(resp);
            Assert.True(t.DnsTimeUs >= 0, $"DnsTimeUs={t.DnsTimeUs}");
            Assert.True(t.ConnectTimeUs >= 0, $"ConnectTimeUs={t.ConnectTimeUs}");
            Assert.True(t.FirstByteTimeUs > 0, $"FirstByteTimeUs={t.FirstByteTimeUs}");
            Assert.True(t.TotalTimeUs > 0, $"TotalTimeUs={t.TotalTimeUs}");
            Assert.True(t.TotalTimeUs >= t.FirstByteTimeUs,
                $"TotalTime ({t.TotalTimeUs}) should >= FirstByte ({t.FirstByteTimeUs})");
            Assert.True(t.DownloadBytes > 0, $"DownloadBytes={t.DownloadBytes}");
            Assert.True(t.DownloadSpeedBps > 0, $"DownloadSpeedBps={t.DownloadSpeedBps}");
            // TLS should be 0 for plain HTTP
            Assert.Equal(0, t.TlsTimeUs);
        }

        [Fact]
        public async Task Timing_TlsTime_NonZero_ForHttps()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.VerifySSL = false;
            client.PreferredVersion = HttpVersion.Default;

            using var resp = await client.GetAsync($"{_server.HttpsUrl}/hello");

            var t = client.Diagnostics.GetTiming(resp);
            Assert.True(t.TlsTimeUs > 0, $"TlsTimeUs should be > 0 for HTTPS, got {t.TlsTimeUs}");
            Assert.True(t.ConnectTimeUs > 0, $"ConnectTimeUs={t.ConnectTimeUs}");
        }

        [Fact]
        public async Task Timing_RedirectTime_NonZero_ForRedirect()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            using var resp = await client.GetAsync($"{_server.HttpUrl}/redirect/2");

            var t = client.Diagnostics.GetTiming(resp);
            Assert.True(t.RedirectTimeUs > 0,
                $"RedirectTimeUs should be > 0 for redirected request, got {t.RedirectTimeUs}");
        }

        [Fact]
        public async Task Timing_UploadBytes_ForPostRequest()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            var body = new byte[5000];
            using var resp = await client.PostAsync($"{_server.HttpUrl}/echo", body, "application/octet-stream");

            var t = client.Diagnostics.GetTiming(resp);
            Assert.True(t.UploadBytes > 0, $"UploadBytes should be > 0 for POST, got {t.UploadBytes}");
        }

        [Fact]
        public async Task Snapshot_ConnectionReuse_WithMultipleRequests()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            // Send 5 sequential requests to the same server
            for (int i = 0; i < 5; i++)
            {
                using var resp = await client.GetAsync($"{_server.HttpUrl}/hello");
            }

            var snapshot = client.Diagnostics.GetSnapshot();
            Assert.Equal(5, snapshot.TotalRequests);
            Assert.True(snapshot.UniqueConnections > 0, "Should have at least 1 connection");
            Assert.True(snapshot.TotalDownloadBytes > 0, $"TotalDownloadBytes={snapshot.TotalDownloadBytes}");
            Assert.True(snapshot.AvgDnsTimeUs >= 0, $"AvgDnsTimeUs={snapshot.AvgDnsTimeUs}");
            Assert.True(snapshot.AvgConnectTimeUs >= 0, $"AvgConnectTimeUs={snapshot.AvgConnectTimeUs}");
            Assert.True(snapshot.AvgFirstByteTimeUs > 0, $"AvgFirstByteTimeUs={snapshot.AvgFirstByteTimeUs}");
            Assert.True(snapshot.AvgTotalTimeUs > 0, $"AvgTotalTimeUs={snapshot.AvgTotalTimeUs}");
        }

        [Fact]
        public async Task Snapshot_Reset_ClearsAllCounters()
        {
            using var client = new CurlHttpClient(enableDiagnostics: true);
            client.PreferredVersion = HttpVersion.Default;

            using var resp = await client.GetAsync($"{_server.HttpUrl}/hello");

            var before = client.Diagnostics.GetSnapshot();
            Assert.Equal(1, before.TotalRequests);

            client.Diagnostics.Reset();

            var after = client.Diagnostics.GetSnapshot();
            Assert.Equal(0, after.TotalRequests);
            Assert.Equal(0, after.SuccessRequests);
            Assert.Equal(0, after.FailedRequests);
            Assert.Equal(0, after.TotalDownloadBytes);
            Assert.Equal(0, after.AvgTotalTimeUs);
        }

        [Fact]
        public async Task Diagnostics_Null_WhenNotEnabled()
        {
            using var client = new CurlHttpClient(enableDiagnostics: false);
            Assert.Null(client.Diagnostics);

            // Should still work without diagnostics
            using var resp = await client.GetAsync($"{_server.HttpUrl}/hello");
        }
    }
}
