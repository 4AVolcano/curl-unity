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

        [Theory]
        [InlineData("X-Evil\r\nInjected: 1", "v")]                  // name 注入
        [InlineData("Authorization", "Bearer x\r\nInjected: 1")]    // value 注入（token 来自外部的典型场景）
        [InlineData("X-A", "v\ninjected")]                          // 裸 LF 同样拒绝
        public async Task SendAsync_HeaderWithCrLf_FailsFast(string name, string value)
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            var request = new HttpRequest
            {
                Url = "http://example.invalid/",
                Headers = new[] { new System.Collections.Generic.KeyValuePair<string, string>(name, value) },
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(request));
            Assert.Contains("CR/LF", ex.Message);
        }

        [Fact]
        public async Task SendAsync_ProxyCredentialWithCrLf_FailsFast()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);
            client.SetProxy(new HttpProxy("http://proxy.example:8080",
                new System.Net.NetworkCredential("user", "p\r\nwd")));

            var request = new HttpRequest { Url = "http://example.invalid/" };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(request));
            Assert.Contains("CR/LF", ex.Message);
        }

        [Fact]
        public async Task SendAsync_DefaultRedirectPolicy_FollowsWithCap30()
        {
            var api = new FakeCurlApi();
            IntPtr captured = IntPtr.Zero;
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                {
                    captured = handle;
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
                }
            };
            using var client = new CurlHttpClient(api);

            using var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                .WaitAsync(TimeSpan.FromSeconds(5));

            var state = api.GetEasyHandleState(captured);
            Assert.Equal(1, state.LongOptions[CurlNative.CURLOPT_FOLLOWLOCATION]);
            Assert.Equal(30, state.LongOptions[CurlNative.CURLOPT_MAXREDIRS]);
        }

        [Fact]
        public async Task SendAsync_FollowRedirectsDisabled_SetsFollowLocationZero()
        {
            var api = new FakeCurlApi();
            IntPtr captured = IntPtr.Zero;
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                {
                    captured = handle;
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
                }
            };
            using var client = new CurlHttpClient(api);

            using var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/", FollowRedirects = false })
                .WaitAsync(TimeSpan.FromSeconds(5));

            var state = api.GetEasyHandleState(captured);
            Assert.Equal(0, state.LongOptions[CurlNative.CURLOPT_FOLLOWLOCATION]);
            Assert.False(state.LongOptions.ContainsKey(CurlNative.CURLOPT_MAXREDIRS));
        }

        [Fact]
        public async Task SendAsync_MaxRedirectsBelowMinusOne_FailsFast()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SendAsync(
                new HttpRequest { Url = "http://example.invalid/", MaxRedirects = -2 }));
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
