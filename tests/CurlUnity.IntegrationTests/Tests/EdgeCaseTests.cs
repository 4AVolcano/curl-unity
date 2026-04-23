using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class EdgeCaseTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public EdgeCaseTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task Post_BinaryBody_RoundTrips()
        {
            // Binary data with all byte values including null bytes
            var body = new byte[256];
            for (int i = 0; i < 256; i++) body[i] = (byte)i;

            using var resp = await _client.PostAsync(
                $"{_server.HttpUrl}/echo-bytes", body, "application/octet-stream");

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(body, resp.Body);
        }

        [Fact]
        public async Task Post_EmptyBody_Succeeds()
        {
            using var resp = await _client.PostAsync(
                $"{_server.HttpUrl}/echo", Array.Empty<byte>(), "text/plain");

            Assert.Equal(200, resp.StatusCode);
        }

        [Fact]
        public async Task LargeTransfer_1MB()
        {
            const int size = 1_000_000;
            using var resp = await _client.GetAsync($"{_server.HttpUrl}/bytes/{size}");

            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(size, resp.Body.Length);
        }

        [Fact]
        public async Task Response_AccessAfterDispose_ReturnsDefaults()
        {
            var resp = await _client.GetAsync($"{_server.HttpUrl}/hello");
            Assert.False(resp.IsDisposed);

            resp.Dispose();
            Assert.True(resp.IsDisposed);

            // Disposed response should return safe defaults
            Assert.Null(resp.ContentType);
            Assert.Null(resp.EffectiveUrl);
            Assert.Equal(-1, resp.ContentLength);
            Assert.Equal(0, resp.RedirectCount);
        }

        [Fact]
        public async Task Response_DoubleDispose_DoesNotThrow()
        {
            var resp = await _client.GetAsync($"{_server.HttpUrl}/hello");
            resp.Dispose();
            resp.Dispose(); // should not throw
            Assert.True(resp.IsDisposed);
        }
    }
}
