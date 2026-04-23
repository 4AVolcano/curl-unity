using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

            var snapshot = client.Diagnostics.GetSnapshot();
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
