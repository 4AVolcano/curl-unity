using System;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.Native;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    // GET/HEAD + body 的 fail-fast 校验：libcurl 的 COPYPOSTFIELDS 会把请求隐式
    // 改写成 POST，必须在提交前拦下，而不是让 "GET + Body" 静默变成 POST 上线。
    [Collection("CurlGlobal")]
    public class HttpRequestValidationTests
    {
        [Theory]
        [InlineData(HttpMethod.Get)]
        [InlineData(HttpMethod.Head)]
        public async Task SendAsync_BodyOnGetOrHead_FailsFast(HttpMethod method)
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            var request = new HttpRequest
            {
                Method = method,
                Url = "http://example.invalid/",
                Body = new byte[] { 1, 2, 3 },
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendAsync(request));
            Assert.Contains("不允许带 body", ex.Message);
        }

        [Fact]
        public async Task SendAsync_EmptyBodyOnGet_IsAllowed()
        {
            // 空 byte[] 不会设置 POSTFIELDS，不触发方法改写，维持向后兼容
            var api = new FakeCurlApi();
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };
            using var client = new CurlHttpClient(api);

            var request = new HttpRequest
            {
                Method = HttpMethod.Get,
                Url = "http://example.invalid/",
                Body = Array.Empty<byte>(),
            };

            using var resp = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(resp);
        }
    }
}
