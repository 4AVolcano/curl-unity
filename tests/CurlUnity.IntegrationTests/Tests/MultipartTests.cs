using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class MultipartTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public MultipartTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient();
            _client.PreferredVersion = CurlUnity.Http.HttpVersion.Default;
        }

        public void Dispose() => _client.Dispose();

        [Fact]
        public async Task PostMultipart_TextFields_AreReceivedByServer()
        {
            var form = new MultipartFormData();
            form.AddText("userId", "42");
            form.AddText("name", "alice");

            using var resp = await _client.PostMultipartAsync(
                $"{_server.HttpUrl}/multipart-echo", form);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("ct=multipart/form-data; boundary=", body);
            Assert.Contains("field:name=alice", body);
            Assert.Contains("field:userId=42", body);
        }

        [Fact]
        public async Task PostMultipart_WithFile_ServerReceivesCorrectBytes()
        {
            var fileBytes = new byte[4096];
            new Random(42).NextBytes(fileBytes);
            var expectedHex = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

            var form = new MultipartFormData();
            form.AddText("desc", "avatar upload");
            form.AddFile("avatar", "photo.jpg", fileBytes, "image/jpeg");

            using var resp = await _client.PostMultipartAsync(
                $"{_server.HttpUrl}/multipart-echo", form);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("field:desc=avatar upload", body);
            Assert.Contains("file:avatar;filename=photo.jpg;ct=image/jpeg;len=4096;sha256=" + expectedHex, body);
        }

        [Fact]
        public async Task PostMultipart_MultipleFiles_AllReceived()
        {
            var a = new byte[] { 1, 2, 3 };
            var b = new byte[] { 4, 5, 6, 7 };

            var form = new MultipartFormData();
            form.AddFile("f1", "a.bin", a);
            form.AddFile("f2", "b.bin", b);

            using var resp = await _client.PostMultipartAsync(
                $"{_server.HttpUrl}/multipart-echo", form);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("file:f1;filename=a.bin;ct=application/octet-stream;len=3;", body);
            Assert.Contains("file:f2;filename=b.bin;ct=application/octet-stream;len=4;", body);
        }

        [Fact]
        public void ContentType_AvailableBeforeBuild()
        {
            var form = new MultipartFormData();
            // ContentType 在构造时即可读,不需要先调 Build
            Assert.StartsWith("multipart/form-data; boundary=", form.ContentType);
        }

        [Fact]
        public void AddFile_ContentTypeWithCRLF_Rejected()
        {
            var form = new MultipartFormData();
            Assert.Throws<ArgumentException>(() =>
                form.AddFile("f", "a.bin", new byte[] { 1 }, "text/plain\r\nX-Injected: evil"));
        }

        // ----------------------------------------------------------------
        // 流式 multipart (Stream part)
        // ----------------------------------------------------------------

        [Fact]
        public async Task PostMultipart_StreamPart_ServerReceivesCorrectBytes()
        {
            // 8 KB,用 Stream 提交,走流式路径(PostMultipartAsync 自动路由)
            var fileBytes = new byte[8192];
            new Random(123).NextBytes(fileBytes);
            var expectedHex = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

            var form = new MultipartFormData();
            form.AddText("desc", "streamed upload");
            using var src = new MemoryStream(fileBytes);
            form.AddFile("avatar", "big.bin", src, src.Length, "application/octet-stream");

            Assert.True(form.HasStreamParts);
            Assert.True(form.ContentLength > fileBytes.Length); // body 含 boundary/headers 开销

            using var resp = await _client.PostMultipartAsync(
                $"{_server.HttpUrl}/multipart-echo", form);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("field:desc=streamed upload", body);
            Assert.Contains("file:avatar;filename=big.bin;ct=application/octet-stream;len=8192;sha256=" + expectedHex, body);
        }

        [Fact]
        public async Task PostMultipart_MixedByteArrayAndStreamParts_AllReceived()
        {
            var a = new byte[] { 1, 2, 3 };
            var b = new byte[2048];
            new Random(7).NextBytes(b);
            var bHex = Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

            var form = new MultipartFormData();
            form.AddText("k", "v");
            form.AddFile("f1", "a.bin", a);                       // byte[] part
            using var bStream = new MemoryStream(b);
            form.AddFile("f2", "b.bin", bStream, bStream.Length); // stream part

            using var resp = await _client.PostMultipartAsync(
                $"{_server.HttpUrl}/multipart-echo", form);

            Assert.Equal(200, resp.StatusCode);
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("field:k=v", body);
            Assert.Contains("file:f1;filename=a.bin;ct=application/octet-stream;len=3;", body);
            Assert.Contains("file:f2;filename=b.bin;ct=application/octet-stream;len=2048;sha256=" + bHex, body);
        }

        [Fact]
        public void Build_WithStreamParts_Throws()
        {
            var form = new MultipartFormData();
            using var src = new MemoryStream(new byte[] { 1, 2, 3 });
            form.AddFile("f", "a.bin", src, src.Length);

            Assert.Throws<InvalidOperationException>(() => form.Build());
        }

        [Fact]
        public void ContentLength_MatchesBuildStreamOutput()
        {
            // 预算长度必须等于实际读出长度,否则 Content-Length 与 body 不一致会让 server 报错
            var form = new MultipartFormData();
            form.AddText("k1", "v1");
            form.AddFile("inline", "a.bin", new byte[] { 1, 2, 3, 4 });
            using var src = new MemoryStream(new byte[1024]);
            form.AddFile("stream", "big.bin", src, src.Length);

            var expectedLength = form.ContentLength;

            using var ms = new MemoryStream();
            using (var s = form.BuildStream())
            {
                var buf = new byte[256];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            }
            Assert.Equal(expectedLength, ms.Length);
        }

        [Fact]
        public void Build_AfterCalled_Add_Throws()
        {
            var form = new MultipartFormData();
            form.AddText("a", "1");
            _ = form.Build();

            Assert.Throws<InvalidOperationException>(() => form.AddText("b", "2"));
            Assert.Throws<InvalidOperationException>(() => form.AddFile("f", "x.bin", new byte[] { 1 }));
        }

        [Fact]
        public void BuildStream_AfterCalled_Add_Throws()
        {
            var form = new MultipartFormData();
            form.AddText("a", "1");
            using (form.BuildStream()) { }

            Assert.Throws<InvalidOperationException>(() => form.AddText("b", "2"));
            using var src = new MemoryStream(new byte[] { 1 });
            Assert.Throws<InvalidOperationException>(() => form.AddFile("f", "x.bin", src, 1));
        }

        [Fact]
        public void Build_CalledTwice_ProducesIdenticalOutput()
        {
            // freeze 不阻止重复 Build, 两次产出必须一致(boundary 构造时固定)
            var form = new MultipartFormData();
            form.AddText("a", "1");
            form.AddFile("f", "a.bin", new byte[] { 1, 2, 3 });

            var first = form.Build();
            var second = form.Build();
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task PostMultipart_StreamEndsEarly_UploadFails()
        {
            // 声明 length=100 但 stream 只能给 50 → MultipartStream 里会抛 IOException
            var form = new MultipartFormData();
            using var src = new MemoryStream(new byte[50]);
            form.AddFile("short", "x.bin", src, 100);

            var ex = await Assert.ThrowsAsync<IOException>(() =>
                _client.PostMultipartAsync($"{_server.HttpUrl}/multipart-echo", form));
            Assert.Contains("stream ended", ex.Message);
        }

        [Fact]
        public async Task PostMultipart_FieldNameWithTrailingBackslash_EscapedSafely()
        {
            // 末尾 '\' 若不 escape 会吃掉闭合引号,导致 parser 歧义。
            // 我们的 EscapeFormName 把 '\' → %5C,服务器解析到的字段名应含 '\'。
            var form = new MultipartFormData();
            form.AddText("weird\\", "val");

            using var resp = await _client.PostMultipartAsync(
                $"{_server.HttpUrl}/multipart-echo", form);

            Assert.Equal(200, resp.StatusCode);
            // RFC 7578 的 name 百分号编码是按字面 escape 机制,server 不做 URL decode;
            // 服务端看到的字段名就是 "weird%5C"。关键是: value 能正确关联到该字段,
            // 说明 quoted-string 没被 '\' 破坏闭合。
            var body = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("field:weird%5C=val", body);
        }
    }
}
