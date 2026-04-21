using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class StreamingTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public StreamingTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task OnDataReceived_ReceivesAllBytes()
        {
            const int size = 50_000;
            var chunks = new List<byte[]>();

            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/bytes/{size}",
                OnDataReceived = (buf, offset, len) =>
                {
                    var chunk = new byte[len];
                    Buffer.BlockCopy(buf, offset, chunk, 0, len);
                    chunks.Add(chunk);
                }
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Null(resp.Body); // Body is null in streaming mode
            Assert.True(chunks.Count > 0);
            Assert.Equal(size, chunks.Sum(c => c.Length));
        }

        [Fact]
        public async Task OnDataReceived_CallbackInvokedMultipleTimes()
        {
            const int size = 100_000;
            int callbackCount = 0;

            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/bytes/{size}",
                OnDataReceived = (buf, offset, len) =>
                {
                    callbackCount++;
                }
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.True(callbackCount > 0);
        }

        // ----------------------------------------------------------------
        // 流式上传(BodyStream)
        // ----------------------------------------------------------------

        [Fact]
        public async Task BodyStream_KnownLength_ServerReceivesExactBytes()
        {
            var bytes = new byte[64 * 1024];
            new Random(42).NextBytes(bytes);
            var expectedHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            using var src = new MemoryStream(bytes);
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                BodyStream = src,
                BodyLength = src.Length,
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(bytes.Length, resp.Body.Length);
            var actualHex = Convert.ToHexString(SHA256.HashData(resp.Body)).ToLowerInvariant();
            Assert.Equal(expectedHex, actualHex);
        }

        [Fact]
        public async Task BodyStream_UnknownLength_ChunkedUploadSucceeds()
        {
            var bytes = new byte[8000];
            new Random(7).NextBytes(bytes);
            var expectedHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            // 故意不传 BodyLength → libcurl 走 chunked transfer-encoding
            using var src = new MemoryStream(bytes);
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                BodyStream = src,
                // BodyLength 留 null
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            var actualHex = Convert.ToHexString(SHA256.HashData(resp.Body)).ToLowerInvariant();
            Assert.Equal(expectedHex, actualHex);
        }

        [Fact]
        public async Task BodyStream_LargePayload_MultipleReadCallbacks()
        {
            // 2 MB — libcurl 内部 buffer 通常 16 KB,足够触发多次 READFUNCTION
            var bytes = new byte[2 * 1024 * 1024];
            new Random(99).NextBytes(bytes);

            using var src = new MemoryStream(bytes);
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                BodyStream = src,
                BodyLength = src.Length,
            };

            using var resp = await _client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.Equal(bytes.Length, resp.Body.Length);
        }

        [Fact]
        public async Task BodyStream_AndBody_BothSet_Throws()
        {
            using var src = new MemoryStream(new byte[] { 1, 2, 3 });
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                Body = new byte[] { 4, 5, 6 },
                BodyStream = src,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SendAsync(req));
        }

        [Fact]
        public async Task BodyStream_AndEmptyBody_BothSet_Throws()
        {
            // 零长度 byte[] 也与 BodyStream 互斥(避免 Length>0 判断误放过)
            using var src = new MemoryStream(new byte[] { 1, 2, 3 });
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                Body = Array.Empty<byte>(),
                BodyStream = src,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SendAsync(req));
        }

        [Fact]
        public async Task BodyStream_NegativeLength_Throws()
        {
            using var src = new MemoryStream(new byte[] { 1, 2, 3 });
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                BodyStream = src,
                BodyLength = -1,
            };

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SendAsync(req));
        }

        [Fact]
        public async Task BodyStream_WithGetMethod_Throws()
        {
            using var src = new MemoryStream(new byte[] { 1, 2, 3 });
            var req = new HttpRequest
            {
                Method = HttpMethod.Get,
                Url = $"{_server.HttpUrl}/hello",
                BodyStream = src,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SendAsync(req));
        }

        [Fact]
        public async Task BodyStream_StreamReadThrows_ExceptionPropagates()
        {
            using var src = new ThrowingStream(failAfter: 100, failure: new IOException("simulated I/O fault"));
            var req = new HttpRequest
            {
                Method = HttpMethod.Post,
                Url = $"{_server.HttpUrl}/echo-bytes",
                BodyStream = src,
                BodyLength = 10_000, // 声明长度 > 100,保证 libcurl 会继续拉触发异常
            };

            var ex = await Assert.ThrowsAsync<IOException>(() => _client.SendAsync(req));
            Assert.Equal("simulated I/O fault", ex.Message);
        }

        /// <summary>前 N 字节正常返回,之后 Read 抛异常。用于验证流式上传的异常传播。</summary>
        private sealed class ThrowingStream : Stream
        {
            private readonly int _failAfter;
            private readonly Exception _failure;
            private int _delivered;

            public ThrowingStream(int failAfter, Exception failure)
            {
                _failAfter = failAfter;
                _failure = failure;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _delivered; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_delivered >= _failAfter) throw _failure;
                int remaining = _failAfter - _delivered;
                int n = Math.Min(count, remaining);
                for (int i = 0; i < n; i++) buffer[offset + i] = (byte)(_delivered + i);
                _delivered += n;
                return n;
            }
        }
    }
}
