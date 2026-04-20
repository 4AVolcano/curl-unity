using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class CookieTests
    {
        private readonly TestServerFixture _server;

        public CookieTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
        }

        private CurlHttpClient NewClient()
        {
            var c = new CurlHttpClient();
            c.PreferredVersion = HttpVersion.Default;
            return c;
        }

        // ---------- 单请求内（redirect chain）cookie 生效 ----------

        [Fact]
        public async Task EnableCookies_PersistsThroughRedirect()
        {
            using var client = NewClient();
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie-and-redirect",
                EnableCookies = true,
            };

            using var resp = await client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            var json = JsonDocument.Parse(resp.Body);
            Assert.True(json.RootElement.GetProperty("hasCookie").GetBoolean());
            Assert.Equal("cookie_value", json.RootElement.GetProperty("value").GetString());
        }

        [Fact]
        public async Task DisabledCookies_NoCookieThroughRedirect()
        {
            using var client = NewClient();
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie-and-redirect",
                EnableCookies = false,
            };

            using var resp = await client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            var json = JsonDocument.Parse(resp.Body);
            Assert.False(json.RootElement.GetProperty("hasCookie").GetBoolean());
        }

        // ---------- 跨请求 cookie 共享（核心需求） ----------

        [Fact]
        public async Task EnableCookies_SharedAcrossSeparateRequestsOnSameClient()
        {
            using var client = NewClient();

            using var setResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie",
                EnableCookies = true,
            });
            Assert.True(setResp.HasResponse);
            Assert.Equal(200, setResp.StatusCode);

            using var checkResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/check-cookie",
                EnableCookies = true,
            });
            Assert.True(checkResp.HasResponse);
            Assert.Equal(200, checkResp.StatusCode);

            var json = JsonDocument.Parse(checkResp.Body);
            Assert.True(json.RootElement.GetProperty("hasCookie").GetBoolean());
            Assert.Equal("cookie_value", json.RootElement.GetProperty("value").GetString());
        }

        [Fact]
        public async Task EnableCookies_MultipleCookiesAllReplayed()
        {
            using var client = NewClient();

            using var r1 = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie/alpha/one",
                EnableCookies = true,
            });
            Assert.True(r1.HasResponse);

            using var r2 = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie/beta/two",
                EnableCookies = true,
            });
            Assert.True(r2.HasResponse);

            using var checkResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/cookies",
                EnableCookies = true,
            });
            Assert.True(checkResp.HasResponse);

            var cookies = JsonSerializer.Deserialize<Dictionary<string, string>>(checkResp.Body);
            Assert.Equal("one", cookies["alpha"]);
            Assert.Equal("two", cookies["beta"]);
        }

        // ---------- Client 之间隔离 ----------

        [Fact]
        public async Task Cookies_IsolatedBetweenClients()
        {
            using var clientA = NewClient();
            using var clientB = NewClient();

            using var setResp = await clientA.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie",
                EnableCookies = true,
            });
            Assert.True(setResp.HasResponse);

            // clientB 必须拿不到 clientA 的 cookie
            using var checkResp = await clientB.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/check-cookie",
                EnableCookies = true,
            });
            Assert.True(checkResp.HasResponse);

            var json = JsonDocument.Parse(checkResp.Body);
            Assert.False(json.RootElement.GetProperty("hasCookie").GetBoolean());
        }

        // ---------- EnableCookies=false 的请求不读写 jar ----------

        [Fact]
        public async Task DisabledOnRequest_DoesNotSendStoredCookie()
        {
            using var client = NewClient();

            using var setResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie",
                EnableCookies = true,
            });
            Assert.True(setResp.HasResponse);

            // 第二个请求禁用了 cookie engine，即使 jar 里有 cookie，也不应带出
            using var checkResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/check-cookie",
                EnableCookies = false,
            });
            Assert.True(checkResp.HasResponse);

            var json = JsonDocument.Parse(checkResp.Body);
            Assert.False(json.RootElement.GetProperty("hasCookie").GetBoolean());
        }

        [Fact]
        public async Task DisabledOnRequest_ResponseCookieIsNotStoredInJar()
        {
            using var client = NewClient();

            // 首个请求禁用 cookie engine，服务器返回的 Set-Cookie 不应进入 jar
            using var setResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie",
                EnableCookies = false,
            });
            Assert.True(setResp.HasResponse);

            // 后续启用 cookie 的请求也不会带上这条 cookie
            using var checkResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/check-cookie",
                EnableCookies = true,
            });
            Assert.True(checkResp.HasResponse);

            var json = JsonDocument.Parse(checkResp.Body);
            Assert.False(json.RootElement.GetProperty("hasCookie").GetBoolean());
        }

        // ---------- 并发 ----------

        [Fact]
        public async Task ConcurrentRequests_CookieJarRemainsConsistent()
        {
            using var client = NewClient();

            const int N = 20;
            var setTasks = Enumerable.Range(0, N).Select(i => client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/set-cookie/k{i}/v{i}",
                EnableCookies = true,
            })).ToArray();

            var setResps = await Task.WhenAll(setTasks);
            try
            {
                foreach (var r in setResps)
                    Assert.True(r.HasResponse);
            }
            finally
            {
                foreach (var r in setResps) r.Dispose();
            }

            using var checkResp = await client.SendAsync(new HttpRequest
            {
                Url = $"{_server.HttpUrl}/cookies",
                EnableCookies = true,
            });
            Assert.True(checkResp.HasResponse);

            var cookies = JsonSerializer.Deserialize<Dictionary<string, string>>(checkResp.Body);
            for (int i = 0; i < N; i++)
                Assert.Equal($"v{i}", cookies[$"k{i}"]);
        }
    }
}
