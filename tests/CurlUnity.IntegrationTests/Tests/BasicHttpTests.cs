using System;
using System.Text;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class BasicHttpTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public BasicHttpTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task Get_Hello_Returns200WithBody()
        {
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/hello");

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal("Hello, World!", Encoding.UTF8.GetString(resp.Body));
        }

        [Fact]
        public async Task Get_StatusCode404_ReturnsCorrectCode()
        {
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/status/404");

            Assert.Equal(404, resp.StatusCode);
        }

        [Fact]
        public async Task Get_StatusCode500_ReturnsCorrectCode()
        {
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/status/500");

            Assert.Equal(500, resp.StatusCode);
        }

        [Fact]
        public async Task Post_Echo_ReturnsSameBody()
        {
            var body = "Hello from test!";
            using var resp = await _client.PostAsync(
                $"{_server.HttpUrl}/echo", Encoding.UTF8.GetBytes(body), "text/plain");

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(body, Encoding.UTF8.GetString(resp.Body));
        }

        [Fact]
        public async Task Post_JsonEcho_ReturnsSameJson()
        {
            var json = "{\"key\":\"value\",\"num\":42}";
            using var resp = await _client.PostJsonAsync(
                $"{_server.HttpUrl}/echo", json);

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(json, Encoding.UTF8.GetString(resp.Body));
        }

        [Fact]
        public async Task Get_Json_ReturnsJsonResponse()
        {
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/json");

            Assert.Equal(200, resp.StatusCode);
            Assert.Contains("application/json", resp.ContentType);
            var text = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("\"message\"", text);
            Assert.Contains("\"ok\"", text);
        }

        [Fact]
        public async Task Get_LargeBody_ReceivesAllBytes()
        {
            const int size = 100_000;
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/bytes/{size}");

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(size, resp.Body.Length);
        }

        [Fact]
        public async Task ContentLength_MatchesActualBody()
        {
            const int size = 5000;
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/bytes/{size}");

            Assert.Equal(size, resp.ContentLength);
            Assert.Equal(resp.ContentLength, resp.Body.Length);
        }

        [Fact]
        public async Task ContentLength_UnknownForChunkedResponse()
        {
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/hello");

            // Kestrel uses chunked transfer for text, so Content-Length is unknown
            Assert.Equal(-1, resp.ContentLength);
        }
    }
}
