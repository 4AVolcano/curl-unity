using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class ConvenienceTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public ConvenienceTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = CurlUnity.Http.HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        // ----------------------------------------------------------------
        // PostFormAsync
        // ----------------------------------------------------------------

        [Fact]
        public async Task PostForm_SimpleFields_EncodedAsUrlForm()
        {
            var fields = new Dictionary<string, string>
            {
                ["username"] = "alice",
                ["password"] = "p@ss w/d!",
            };

            using var resp = await _client.PostFormAsync($"{_server.HttpUrl}/echo", fields);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            // 顺序来自 Dictionary 的枚举序(实际 .NET 里是插入序);断言两个片段都在
            Assert.Contains("username=alice", body);
            // 空格 → %20 或 +, Uri.EscapeDataString 产出 %20;special 字符 percent-encoded
            Assert.Contains("password=p%40ss%20w%2Fd%21", body);
            Assert.Contains("&", body); // 两个 field 之间有分隔符
        }

        [Fact]
        public async Task PostForm_DuplicateKeys_Preserved()
        {
            // IEnumerable<KV> 支持重复 key,比如 OAuth scope=a&scope=b
            var fields = new[]
            {
                new KeyValuePair<string, string>("scope", "read"),
                new KeyValuePair<string, string>("scope", "write"),
            };

            using var resp = await _client.PostFormAsync($"{_server.HttpUrl}/echo", fields);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Equal("scope=read&scope=write", body);
        }

        [Fact]
        public async Task PostForm_SetsContentType()
        {
            var fields = new Dictionary<string, string> { ["k"] = "v" };
            // /echo 会把 request Content-Type 原样回响应的 Content-Type
            using var resp = await _client.PostFormAsync($"{_server.HttpUrl}/echo", fields);

            Assert.Equal(200, resp.StatusCode);
            Assert.Contains("application/x-www-form-urlencoded",
                resp.ContentType, StringComparison.OrdinalIgnoreCase);
        }

        // ----------------------------------------------------------------
        // Bearer / Basic 认证
        // ----------------------------------------------------------------

        [Fact]
        public async Task WithBearerToken_SetsAuthorizationHeader()
        {
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/echo-headers" }
                .WithBearerToken("abc.xyz.123");

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            Assert.True(doc.RootElement.TryGetProperty("Authorization", out var auth));
            Assert.Equal("Bearer abc.xyz.123", auth[0].GetString());
        }

        [Fact]
        public async Task WithBasicAuth_EncodesUserPasswordBase64()
        {
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/echo-headers" }
                .WithBasicAuth("alice", "s3cret");

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            Assert.True(doc.RootElement.TryGetProperty("Authorization", out var auth));
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));
            Assert.Equal(expected, auth[0].GetString());
        }

        [Fact]
        public void WithBasicAuth_UsernameWithColon_Throws()
        {
            var req = new HttpRequest { Url = "http://example/" };
            Assert.Throws<ArgumentException>(() => req.WithBasicAuth("al:ice", "pwd"));
        }

        [Fact]
        public void WithBearerToken_EmptyToken_Throws()
        {
            var req = new HttpRequest { Url = "http://example/" };
            Assert.Throws<ArgumentException>(() => req.WithBearerToken(""));
        }

        // ----------------------------------------------------------------
        // UserAgent
        // ----------------------------------------------------------------

        [Fact]
        public async Task UserAgent_Default_SentAsHeader()
        {
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/echo-headers" };
            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            Assert.True(doc.RootElement.TryGetProperty("User-Agent", out var ua));
            Assert.StartsWith("CurlUnity/", ua[0].GetString());
        }

        [Fact]
        public async Task UserAgent_CustomOnClient_Applied()
        {
            using var client = new CurlHttpClient { UserAgent = "MyGame/1.2.3" };
            client.PreferredVersion = CurlUnity.Http.HttpVersion.Default;

            var req = new HttpRequest { Url = $"{_server.HttpUrl}/echo-headers" };
            using var resp = await client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            Assert.True(doc.RootElement.TryGetProperty("User-Agent", out var ua));
            Assert.Equal("MyGame/1.2.3", ua[0].GetString());
        }

        [Fact]
        public async Task UserAgent_RequestHeaderOverridesClientDefault()
        {
            // 请求级 Headers 里写 User-Agent → libcurl slist 覆盖 CURLOPT_USERAGENT
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/echo-headers",
                Headers = new[]
                {
                    new KeyValuePair<string, string>("User-Agent", "PerReqAgent/9.9"),
                },
            };
            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            Assert.True(doc.RootElement.TryGetProperty("User-Agent", out var ua));
            Assert.Equal("PerReqAgent/9.9", ua[0].GetString());
        }
    }
}
