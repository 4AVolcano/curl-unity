using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class HttpMethodTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public HttpMethodTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task Put_SendsBodyAndMethod()
        {
            var body = "put-data";
            using var resp = await _client.PutAsync(
                $"{_server.HttpUrl}/method-echo", Encoding.UTF8.GetBytes(body), "text/plain");

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body);
            Assert.Equal("PUT", json.RootElement.GetProperty("method").GetString());
            Assert.Equal(body, json.RootElement.GetProperty("body").GetString());
        }

        [Fact]
        public async Task Delete_SendsCorrectMethod()
        {
            using var resp = await _client.DeleteAsync($"{_server.HttpUrl}/method-echo");

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body);
            Assert.Equal("DELETE", json.RootElement.GetProperty("method").GetString());
        }

        [Fact]
        public async Task Patch_SendsBodyAndMethod()
        {
            var body = "patch-data";
            var req = new HttpRequest
            {
                Method = HttpMethod.Patch,
                Url = $"{_server.HttpUrl}/method-echo",
                Body = Encoding.UTF8.GetBytes(body),
                Headers = new[] { new System.Collections.Generic.KeyValuePair<string, string>("Content-Type", "text/plain") }
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body);
            Assert.Equal("PATCH", json.RootElement.GetProperty("method").GetString());
            Assert.Equal(body, json.RootElement.GetProperty("body").GetString());
        }

        [Fact]
        public async Task Head_ReturnsHeadersWithoutBody()
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Head,
                Url = $"{_server.HttpUrl}/hello",
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            // HEAD response must have no body
            Assert.True(resp.Body == null || resp.Body.Length == 0);
        }

        [Fact]
        public async Task Options_SendsCorrectMethod()
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Options,
                Url = $"{_server.HttpUrl}/method-echo",
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body);
            Assert.Equal("OPTIONS", json.RootElement.GetProperty("method").GetString());
        }
    }
}
