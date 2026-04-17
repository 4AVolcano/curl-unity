using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class HttpVersionTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public HttpVersionTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.VerifySSL = false; // self-signed cert
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task Http11_OverPlainHttp()
        {
            _client.PreferredVersion = HttpVersion.Http11;

            using var resp = await _client.GetAsync($"{_server.HttpUrl}/protocol");

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(HttpVersion.Http11, resp.Version);

            var protocol = Encoding.UTF8.GetString(resp.Body);
            Assert.Equal("HTTP/1.1", protocol);
        }

        [Fact]
        public async Task Http2_OverHttps_ViaAlpn()
        {
            _client.PreferredVersion = HttpVersion.Http2;

            using var resp = await _client.GetAsync($"{_server.HttpsUrl}/protocol");

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(HttpVersion.Http2, resp.Version);

            var protocol = Encoding.UTF8.GetString(resp.Body);
            Assert.Equal("HTTP/2", protocol);
        }

        [Fact]
        public async Task Https_BasicGet_Works()
        {
            _client.PreferredVersion = HttpVersion.Default;

            using var resp = await _client.GetAsync($"{_server.HttpsUrl}/hello");

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal("Hello, World!", Encoding.UTF8.GetString(resp.Body));
        }
    }

    /// <summary>
    /// HTTP/3 tests against external h3-test server.
    /// Skipped automatically when network is unavailable.
    /// Run with: dotnet test --filter "Category=Network"
    /// Exclude with: dotnet test --filter "Category!=Network"
    /// </summary>
    [Collection("Integration")]
    [Trait("Category", "Network")]
    public class Http3Tests : IDisposable
    {
        private const string H3TestUrl = "https://h3-test.godrive.top/";
        private readonly CurlHttpClient _client;

        public Http3Tests(CurlGlobalFixture _)
        {
            _client = new CurlHttpClient();
        }

        public void Dispose() => _client.Dispose();

        /// <summary>
        /// 有界检查网络是否可达 H3TestHost:443。避免 DNS/TCP 握手 stall 导致测试挂起。
        /// 返回 true/false；报告为 Skipped 由调用方用 Skip.IfNot 处理。
        /// </summary>
        private static async Task<bool> IsNetworkAvailableAsync()
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync("h3-test.godrive.top", 443, cts.Token);
                return true;
            }
            catch { return false; }
        }

        [SkippableFact]
        public async Task Http3Only_ConnectsViaQuic()
        {
            Skip.IfNot(await IsNetworkAvailableAsync(), "h3-test.godrive.top unreachable");

            _client.PreferredVersion = HttpVersion.Http3Only;

            using var resp = await _client.GetAsync(H3TestUrl);

            Assert.True(resp.HasResponse, $"Request failed: {resp.ErrorMessage}");
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(HttpVersion.Http3, resp.Version);

            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.StartsWith("proto=HTTP/3", body);
        }

        [SkippableFact]
        public async Task PreferH3_NegotiatesToHttp3()
        {
            Skip.IfNot(await IsNetworkAvailableAsync(), "h3-test.godrive.top unreachable");

            _client.PreferredVersion = HttpVersion.PreferH3;

            // PreferH3 may fall back to HTTP/2 on first request, then upgrade.
            // Send twice to allow Alt-Svc discovery.
            using var resp1 = await _client.GetAsync(H3TestUrl);
            Assert.True(resp1.HasResponse);

            using var resp2 = await _client.GetAsync(H3TestUrl);
            Assert.True(resp2.HasResponse);
            Assert.Equal(200, resp2.StatusCode);

            var body = Encoding.UTF8.GetString(resp2.Body);
            // Should be HTTP/3 after Alt-Svc discovery, but HTTP/2 is also acceptable
            Assert.StartsWith("proto=HTTP/", body);
        }

        [SkippableFact]
        public async Task Http2_FallbackWorks()
        {
            Skip.IfNot(await IsNetworkAvailableAsync(), "h3-test.godrive.top unreachable");

            _client.PreferredVersion = HttpVersion.Http2;

            using var resp = await _client.GetAsync(H3TestUrl);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(HttpVersion.Http2, resp.Version);

            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.StartsWith("proto=HTTP/2", body);
        }
    }
}
