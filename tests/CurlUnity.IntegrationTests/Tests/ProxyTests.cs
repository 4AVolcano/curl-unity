using System;
using System.Net;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    /// <summary>
    /// Proxy 支持的集成测试。
    /// </summary>
    /// <remarks>
    /// 真实代理用例通过环境变量驱动,未设置时自动 skip,适合本地运行。CI 默认跳过:
    ///   CURL_UNITY_PROXY_URL        必填,形如 "http://127.0.0.1:7890"、"socks5://..."
    ///   CURL_UNITY_PROXY_USER       可选,Basic 认证用户名
    ///   CURL_UNITY_PROXY_PASSWORD   可选,Basic 认证密码
    ///   CURL_UNITY_PROXY_TEST_URL   可选,目标 URL,默认 http://example.com/
    /// 负面用例(代理不可达 / ClearProxy 回退)不依赖外部资源,恒定执行。
    /// </remarks>
    [Collection("Integration")]
    public class ProxyTests : IDisposable
    {
        private static string ProxyUrl      => Environment.GetEnvironmentVariable("CURL_UNITY_PROXY_URL");
        private static string ProxyUser     => Environment.GetEnvironmentVariable("CURL_UNITY_PROXY_USER");
        private static string ProxyPassword => Environment.GetEnvironmentVariable("CURL_UNITY_PROXY_PASSWORD");
        private static string ProxyTestUrl  =>
            Environment.GetEnvironmentVariable("CURL_UNITY_PROXY_TEST_URL") ?? "http://example.com/";

        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public ProxyTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = CurlUnity.Http.HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [SkippableFact]
        public async Task SetProxy_RequestSucceedsThroughLocalProxy()
        {
            Skip.If(string.IsNullOrEmpty(ProxyUrl),
                "CURL_UNITY_PROXY_URL not set; skipping live-proxy test.");

            NetworkCredential creds = null;
            if (!string.IsNullOrEmpty(ProxyUser))
                creds = new NetworkCredential(ProxyUser, ProxyPassword ?? string.Empty);

            _client.SetProxy(new HttpProxy(ProxyUrl, creds));

            // 走外部网络, 给合理超时避免代理/网络异常时测试长时间挂起
            var req = new HttpRequest
            {
                Url = ProxyTestUrl,
                ConnectTimeoutMs = 5000,
                TimeoutMs = 15000,
            };
            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse,
                $"expected response via proxy {ProxyUrl}, got error {resp.ErrorCode}: {resp.ErrorMessage}");
            Assert.InRange(resp.StatusCode, 200, 399);
        }

        [Fact]
        public async Task SetProxy_UnreachableProxy_RequestFails()
        {
            // 127.0.0.1:1 不监听, proxy 连接会失败; libcurl 此时不会绕过 proxy 直连
            _client.SetProxy(new HttpProxy("http://127.0.0.1:1"));

            using var resp = await _client.GetAsync($"{_server.HttpUrl}/hello");

            Assert.False(resp.HasResponse);
            Assert.NotEqual(0, resp.ErrorCode);
        }

        [Fact]
        public async Task ClearProxy_AfterBadProxy_RestoresDirectConnection()
        {
            _client.SetProxy(new HttpProxy("http://127.0.0.1:1"));
            _client.ClearProxy();

            using var resp = await _client.GetAsync($"{_server.HttpUrl}/hello");

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
        }
    }
}
