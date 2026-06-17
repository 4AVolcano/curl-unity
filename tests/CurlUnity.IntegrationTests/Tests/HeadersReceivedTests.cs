using System;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class HeadersReceivedTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public HeadersReceivedTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task CallbackFires_WithCorrectStatusAndContentType()
        {
            IHttpResponse cbResponse = null;
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/json",
                OnHeadersReceived = resp => { cbResponse = resp; }
            };

            using var resp = await _client.SendAsync(req);

            Assert.NotNull(cbResponse);
            Assert.Equal(200, cbResponse.StatusCode);
            Assert.Contains("application/json", cbResponse.ContentType);
        }

        [Fact]
        public async Task BodyIsNull_InCallback_NonNull_AfterSendAsync()
        {
            byte[] cbBody = new byte[1]; // sentinel
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/json",
                OnHeadersReceived = resp => { cbBody = resp.Body; }
            };

            using var resp = await _client.SendAsync(req);

            Assert.Null(cbBody);
            Assert.NotNull(resp.Body);
            Assert.True(resp.Body.Length > 0);
        }

        [Fact]
        public async Task SameInstance_ReturnedBySendAsync()
        {
            IHttpResponse cbResponse = null;
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/hello",
                OnHeadersReceived = resp => { cbResponse = resp; }
            };

            using var resp = await _client.SendAsync(req);

            Assert.Same(cbResponse, resp);
        }

        [Fact]
        public async Task HeadRequest_CallbackStillFires()
        {
            IHttpResponse cbResponse = null;
            var req = new HttpRequest
            {
                Method = HttpMethod.Head,
                Url = $"{_server.HttpUrl}/hello",
                OnHeadersReceived = resp => { cbResponse = resp; }
            };

            using var resp = await _client.SendAsync(req);

            Assert.NotNull(cbResponse);
            Assert.Equal(200, cbResponse.StatusCode);
        }

        [Fact]
        public async Task Status204_CallbackStillFires()
        {
            IHttpResponse cbResponse = null;
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/status/204",
                OnHeadersReceived = resp => { cbResponse = resp; }
            };

            using var resp = await _client.SendAsync(req);

            Assert.NotNull(cbResponse);
            Assert.Equal(204, cbResponse.StatusCode);
        }

        [Fact]
        public async Task WithEnableResponseHeaders_HeadersAvailable()
        {
            bool hadHeaders = false;
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/custom-headers",
                EnableResponseHeaders = true,
                OnHeadersReceived = resp =>
                {
                    hadHeaders = resp.Headers != null && resp.Headers.Count > 0;
                }
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(hadHeaders);
        }

        [Fact]
        public async Task WithoutEnableResponseHeaders_HeadersNull_StatusAvailable()
        {
            bool headersNull = false;
            int statusCode = 0;
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/hello",
                EnableResponseHeaders = false,
                OnHeadersReceived = resp =>
                {
                    headersNull = resp.Headers == null;
                    statusCode = resp.StatusCode;
                }
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(headersNull);
            Assert.Equal(200, statusCode);
        }

        [Fact]
        public async Task ThrowingCallback_PropagatesException()
        {
            var expected = new InvalidOperationException("test abort");
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/hello",
                OnHeadersReceived = _ => throw expected
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _client.SendAsync(req));
            Assert.Same(expected, ex);
        }

        [Fact]
        public async Task NoCallback_BehaviorUnchanged()
        {
            var req = new HttpRequest { Url = $"{_server.HttpUrl}/hello" };
            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);
            Assert.NotNull(resp.Body);
        }

        [Fact]
        public async Task StreamingMode_CallbackFiresBeforeData()
        {
            int cbStatusCode = 0;
            bool cbBodyNull = true;
            bool dataReceivedAfterCallback = false;

            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/bytes/1000",
                OnHeadersReceived = resp =>
                {
                    cbStatusCode = resp.StatusCode;
                    cbBodyNull = resp.Body == null;
                },
                OnDataReceived = (buf, offset, len) =>
                {
                    if (cbStatusCode > 0)
                        dataReceivedAfterCallback = true;
                }
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, cbStatusCode);
            Assert.True(cbBodyNull);
            Assert.True(dataReceivedAfterCallback);
        }

        [Fact]
        public async Task Non200Status_CallbackStillFires()
        {
            IHttpResponse cbResponse = null;
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/status/404",
                OnHeadersReceived = resp => { cbResponse = resp; }
            };

            using var resp = await _client.SendAsync(req);

            Assert.NotNull(cbResponse);
            Assert.Equal(404, cbResponse.StatusCode);
        }
    }
}
