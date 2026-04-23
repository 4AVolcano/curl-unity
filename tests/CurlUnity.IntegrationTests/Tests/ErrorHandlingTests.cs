using System;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class ErrorHandlingTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public ErrorHandlingTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task ConnectionRefused_Throws_ConnectFailed()
        {
            // Port 1 should not be listening
            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => _client.GetAsync("http://localhost:1/nope"));

            Assert.Equal(HttpErrorKind.ConnectFailed, ex.ErrorKind);
            Assert.NotEqual(0, ex.CurlCode); // CURLE_COULDNT_CONNECT = 7
            Assert.False(string.IsNullOrEmpty(ex.Message));
        }

        [Fact]
        public async Task Timeout_Throws_TimeoutKind()
        {
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/delay/5000",
                TimeoutMs = 500,
            };

            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => _client.SendAsync(req));

            Assert.Equal(HttpErrorKind.Timeout, ex.ErrorKind);
            Assert.Equal(28, ex.CurlCode); // CURLE_OPERATION_TIMEDOUT
        }

        [Fact]
        public async Task DnsFailure_Throws_DnsFailed()
        {
            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => _client.GetAsync("http://this.host.does.not.exist.invalid/test"));

            Assert.Equal(HttpErrorKind.DnsFailed, ex.ErrorKind);
            Assert.NotEqual(0, ex.CurlCode);
        }

        [Fact]
        public async Task Exception_MessageContainsKindAndCode()
        {
            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => _client.GetAsync("http://localhost:1/nope"));

            Assert.Contains(ex.ErrorKind.ToString(), ex.Message);
            Assert.Contains(ex.CurlCode.ToString(), ex.Message);
        }

        [Fact]
        public async Task ConnectTimeout_FiresBeforeTotalTimeout()
        {
            // 10.255.255.1 is a non-routable IP — TCP SYN will hang
            var req = new HttpRequest
            {
                Url = "http://10.255.255.1/",
                ConnectTimeoutMs = 2000,
                TimeoutMs = 60000, // total timeout is much larger
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => _client.SendAsync(req));
            sw.Stop();

            // ConnectTimeoutMs 命中时,libcurl 可能返回 CURLE_OPERATION_TIMEDOUT (Timeout),
            // 也可能先命中 OS 层 connect() 返回(ETIMEDOUT/EHOSTUNREACH),被包成
            // CURLE_COULDNT_CONNECT (ConnectFailed) 或 CURLE_SEND/RECV_ERROR (NetworkIo)。
            // 跨平台不稳定,只验证 CurlCode 非 0 + 时间远小于 total timeout。
            Assert.NotEqual(0, ex.CurlCode);
            // Must complete well before total timeout (60s).
            // macOS TCP stack may add OS-level retries, so allow up to 15s.
            Assert.True(sw.ElapsedMilliseconds < 15000,
                $"Expected connect timeout well before 60s total, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task VerifySSL_True_RejectsSelfSignedCert()
        {
            using var client = new CurlHttpClient();
            client.VerifySSL = true; // default, but explicit for clarity

            // Self-signed cert should be rejected. libcurl 在 macOS Secure Transport
            // 后端下把 SecTrust 拒绝映射为 CURLE_COULDNT_CONNECT(ConnectFailed);在
            // OpenSSL/mbedTLS 后端下是 CURLE_PEER_FAILED_VERIFICATION(TlsError)。
            // 跨后端不稳定,只验证请求确实被拒。
            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => client.GetAsync($"{_server.HttpsUrl}/hello"));

            Assert.Contains(ex.ErrorKind, new[] { HttpErrorKind.TlsError, HttpErrorKind.ConnectFailed });
            Assert.NotEqual(0, ex.CurlCode);
        }
    }
}
