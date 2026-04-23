using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class HeaderTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public HeaderTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task RequestHeaders_AreSentToServer()
        {
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/echo-headers",
                Headers = new[]
                {
                    new KeyValuePair<string, string>("X-Test-Header", "test-value"),
                    new KeyValuePair<string, string>("X-Another", "another-value"),
                }
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);

            var json = Encoding.UTF8.GetString(resp.Body);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("X-Test-Header", out var val1));
            Assert.Equal("test-value", val1[0].GetString());

            Assert.True(root.TryGetProperty("X-Another", out var val2));
            Assert.Equal("another-value", val2[0].GetString());
        }

        [Fact]
        public async Task ResponseHeaders_ParsedCorrectly()
        {
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/custom-headers",
                EnableResponseHeaders = true,
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            Assert.NotNull(resp.Headers);

            Assert.True(resp.Headers.ContainsKey("x-custom-one"));
            Assert.Equal("value1", resp.Headers["x-custom-one"][0]);

            Assert.True(resp.Headers.ContainsKey("x-custom-two"));
            Assert.Equal("value2", resp.Headers["x-custom-two"][0]);
        }

        [Fact]
        public async Task ResponseHeaders_Disabled_ReturnsNull()
        {
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/custom-headers",
                EnableResponseHeaders = false,
            };

            using var resp = await _client.SendAsync(req);

            Assert.Null(resp.Headers);
        }

        [Fact]
        public async Task ContentType_ReadFromResponse()
        {
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/json");

            Assert.Contains("application/json", resp.ContentType);
        }
    }
}
