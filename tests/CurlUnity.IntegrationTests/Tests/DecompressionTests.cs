using System;
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
    public class DecompressionTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public DecompressionTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = CurlUnity.Http.HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task AutoDecompress_Default_IsEnabled_BodyIsPlaintext()
        {
            // 默认 AutoDecompressResponse = true;server 会收到 Accept-Encoding
            // 回 gzip, libcurl 透明解压, Body 是解压后的 8192 个 'A'
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/gzip-text/8192" };
            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse,
                $"expected response, got error {resp.ErrorCode}: {resp.ErrorMessage}");
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(8192, resp.Body.Length);
            Assert.Equal(new string('A', 8192), Encoding.UTF8.GetString(resp.Body));
        }

        [Fact]
        public async Task AutoDecompress_Disabled_ServerReturnsPlaintext()
        {
            // AutoDecompressResponse=false → 不发 Accept-Encoding → server 走分支回明文
            // 此时客户端拿到的就是明文
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/gzip-text/8192",
                AutoDecompressResponse = false,
            };
            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(8192, resp.Body.Length);
            Assert.Equal(new string('A', 8192), Encoding.UTF8.GetString(resp.Body));
        }

        [Fact]
        public async Task AutoDecompress_Enabled_SendsAcceptEncodingHeader()
        {
            // /echo-headers 把 request headers 以 JSON 返回; 这里用来确认
            // libcurl 在 AutoDecompressResponse=true 时真的发了 Accept-Encoding
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/echo-headers" };
            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            Assert.True(doc.RootElement.TryGetProperty("Accept-Encoding", out var values),
                "expected Accept-Encoding header in request");
            var joined = string.Join(",", values.EnumerateArray().Select(v => v.GetString()));
            Assert.Contains("gzip", joined, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AutoDecompress_Disabled_DoesNotSendAcceptEncoding()
        {
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/echo-headers",
                AutoDecompressResponse = false,
            };
            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(resp.Body));
            // 关闭后, request 不应带 Accept-Encoding header
            Assert.False(doc.RootElement.TryGetProperty("Accept-Encoding", out _),
                "Accept-Encoding should not be sent when AutoDecompressResponse=false");
        }
    }
}
