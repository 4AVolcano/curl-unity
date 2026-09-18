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

        // 空 body 的 POST 必须发 Content-Length: 0。若 POSTFIELDSIZE 没设置,
        // libcurl 会认为 body 由 read callback 提供, 发出 chunked + Expect: 100-continue,
        // 并把进程 stdin 的内容当请求体传出去。
        [Fact]
        public async Task Post_EmptyBody_SendsContentLengthZero()
        {
            using var resp = await _client.PostAsync(
                $"{_server.HttpUrl}/request-info", Array.Empty<byte>(), "text/plain");

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body).RootElement;
            Assert.Equal("POST", json.GetProperty("method").GetString());
            Assert.Equal("0", json.GetProperty("contentLength").GetString());
            Assert.Equal("", json.GetProperty("transferEncoding").GetString());
            Assert.Equal("", json.GetProperty("expect").GetString());
            Assert.Equal(0, json.GetProperty("bodyLength").GetInt64());
        }

        [Fact]
        public async Task Post_NullBody_SendsContentLengthZero()
        {
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/request-info",
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body).RootElement;
            Assert.Equal("POST", json.GetProperty("method").GetString());
            Assert.Equal("0", json.GetProperty("contentLength").GetString());
            Assert.Equal("", json.GetProperty("transferEncoding").GetString());
            Assert.Equal(0, json.GetProperty("bodyLength").GetInt64());
        }

        // PUT/PATCH 等 CUSTOMREQUEST 方法空 body 时维持原行为: 不带 Content-Length,
        // 也不会被 libcurl 塞上默认的 Content-Type, 但同样不能走 chunked。
        [Theory]
        [InlineData(HttpMethod.Put)]
        [InlineData(HttpMethod.Patch)]
        [InlineData(HttpMethod.Delete)]
        public async Task CustomMethod_EmptyBody_DoesNotUseChunked(HttpMethod method)
        {
            var req = new HttpRequest
            {
                Method = method,
                Url = $"{_server.HttpUrl}/request-info",
                Body = Array.Empty<byte>(),
            };

            using var resp = await _client.SendAsync(req);

            Assert.Equal(200, resp.StatusCode);

            var json = JsonDocument.Parse(resp.Body).RootElement;
            Assert.Equal(method.ToString().ToUpperInvariant(), json.GetProperty("method").GetString());
            Assert.Equal("", json.GetProperty("transferEncoding").GetString());
            Assert.Equal(0, json.GetProperty("bodyLength").GetInt64());
        }
    }
}
