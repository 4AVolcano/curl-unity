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
        public async Task SendAsync_DefaultConnectTimeout_Is30Seconds()
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
            Assert.Equal(30_000, state.LongOptions[CurlNative.CURLOPT_CONNECTTIMEOUT_MS]);
        }

        [Fact]
        public async Task SendAsync_DnsCacheTimeout_UsesCurrentClientValue()
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };
            using var client = new CurlHttpClient(api);

            async Task<long> SendAndReadTimeoutAsync()
            {
                var responseTask = client.SendAsync(
                    new HttpRequest { Url = "http://example.invalid/" });
                var state = api.GetEasyHandleState(api.LastEasyHandle);
                var timeout = state.LongOptions[CurlNative.CURLOPT_DNS_CACHE_TIMEOUT];
                using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
                return timeout;
            }

            Assert.Equal(60, await SendAndReadTimeoutAsync());

            client.DnsCacheTimeoutSeconds = 10;
            Assert.Equal(10, await SendAndReadTimeoutAsync());

            client.DnsCacheTimeoutSeconds = 0;
            Assert.Equal(0, await SendAndReadTimeoutAsync());

            client.DnsCacheTimeoutSeconds = -1;
            Assert.Equal(-1, await SendAndReadTimeoutAsync());
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(int.MinValue)]
        public void DnsCacheTimeoutSeconds_BelowMinusOne_Throws(int value)
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => client.DnsCacheTimeoutSeconds = value);
        }

        [Fact]
        public async Task SendAsync_MaxIdleConnectionAge_UsesCurrentClientValue()
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };
            using var client = new CurlHttpClient(api);

            async Task<long> SendAndReadMaxAgeAsync()
            {
                var responseTask = client.SendAsync(
                    new HttpRequest { Url = "http://example.invalid/" });
                var state = api.GetEasyHandleState(api.LastEasyHandle);
                var maxAge = state.LongOptions[CurlNative.CURLOPT_MAXAGE_CONN];
                using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
                return maxAge;
            }

            Assert.Equal(118, await SendAndReadMaxAgeAsync());

            client.MaxIdleConnectionAgeSeconds = 60;
            Assert.Equal(60, await SendAndReadMaxAgeAsync());

            client.MaxIdleConnectionAgeSeconds = 0;
            Assert.Equal(0, await SendAndReadMaxAgeAsync());
        }

        [Fact]
        public void MaxIdleConnectionAgeSeconds_Negative_Throws()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => client.MaxIdleConnectionAgeSeconds = -1);
        }

        [Fact]
        public async Task SendAsync_IPAddresses_GeneratesResolveRuleAndKeepsSlistUntilCompletion()
        {
            var api = new FakeCurlApi();
            IntPtr resolveSlist = IntPtr.Zero;
            IntPtr headerSlist = IntPtr.Zero;
            System.Collections.Generic.IReadOnlyList<string> resolveValues = null;
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle == IntPtr.Zero)
                    return;

                var state = api.GetEasyHandleState(handle);
                resolveSlist = state.PointerOptions[CurlNative.CURLOPT_RESOLVE];
                headerSlist = state.PointerOptions[CurlNative.CURLOPT_HTTPHEADER];
                resolveValues = api.GetSListValues(resolveSlist);
                api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };
            using var client = new CurlHttpClient(api);

            using var response = await client.SendAsync(new HttpRequest
            {
                Url = "https://example.invalid/",
                Headers = new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("X-Test", "1"),
                },
                IPAddresses = new[]
                {
                    "192.0.2.1",
                    "2001:db8::1",
                },
            }).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[]
            {
                "+example.invalid:443:192.0.2.1,[2001:db8::1]",
            }, resolveValues);
            Assert.NotEqual(IntPtr.Zero, resolveSlist);
            Assert.NotEqual(resolveSlist, headerSlist);
            Assert.False(api.IsSListAlive(resolveSlist));
            Assert.False(api.IsSListAlive(headerSlist));
        }

        [Fact]
        public async Task SendAsync_EmptyIPAddresses_RemovesExistingMapping()
        {
            var api = new FakeCurlApi();
            System.Collections.Generic.IReadOnlyList<string> resolveValues = null;
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                {
                    var state = api.GetEasyHandleState(handle);
                    var resolveSlist = state.PointerOptions[CurlNative.CURLOPT_RESOLVE];
                    resolveValues = api.GetSListValues(resolveSlist);
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
                }
            };
            using var client = new CurlHttpClient(api);

            using var response = await client.SendAsync(new HttpRequest
            {
                Url = "https://example.invalid/",
                IPAddresses = Array.Empty<string>(),
            }).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { "-example.invalid:443" }, resolveValues);
        }

        [Fact]
        public async Task SendAsync_NullIPAddresses_DoesNotModifyDnsMapping()
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };
            using var client = new CurlHttpClient(api);

            var responseTask = client.SendAsync(new HttpRequest
            {
                Url = "https://example.invalid/",
            });
            var state = api.GetEasyHandleState(api.LastEasyHandle);
            Assert.False(state.PointerOptions.ContainsKey(CurlNative.CURLOPT_RESOLVE));
            using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Theory]
        [InlineData("not-an-ip")]
        [InlineData("[2001:db8::1]")]
        public async Task SendAsync_InvalidIPAddress_FailsFast(string value)
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(
                new HttpRequest
                {
                    Url = "https://example.invalid/",
                    IPAddresses = new[] { value },
                }));

            Assert.Contains("IPv6 请勿添加方括号", ex.Message);
            Assert.True(api.GetEasyHandleState(api.LastEasyHandle).IsCleanedUp);
        }

        [Fact]
        public async Task SendAsync_IPAddressesWithIpLiteralUrl_FailsFast()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(
                new HttpRequest
                {
                    Url = "https://192.0.2.10/",
                    IPAddresses = new[] { "192.0.2.1" },
                }));
        }

        [Fact]
        public async Task SendAsync_LowSpeedPair_SetsBothOptions()
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

            using var resp = await client.SendAsync(new HttpRequest
            {
                Url = "http://example.invalid/",
                LowSpeedLimitBytesPerSecond = 1,
                LowSpeedTimeSeconds = 60,
            }).WaitAsync(TimeSpan.FromSeconds(5));

            var state = api.GetEasyHandleState(captured);
            Assert.Equal(1, state.LongOptions[CurlNative.CURLOPT_LOW_SPEED_LIMIT]);
            Assert.Equal(60, state.LongOptions[CurlNative.CURLOPT_LOW_SPEED_TIME]);
        }

        [Fact]
        public async Task SendAsync_LowSpeedOnlyOneSet_FailsFast()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(
                new HttpRequest { Url = "http://example.invalid/", LowSpeedTimeSeconds = 60 }));
        }

        // LowSpeed 负数分支（`< 0` 校验，codecov 报告的 partial）
        [Theory]
        [InlineData(-1, 0)]   // limit 负数
        [InlineData(0, -1)]   // time 负数
        [InlineData(-1, -1)]  // 两者都负
        public async Task SendAsync_LowSpeedNegative_FailsFast(int limit, int time)
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SendAsync(
                new HttpRequest
                {
                    Url = "http://example.invalid/",
                    LowSpeedLimitBytesPerSecond = limit,
                    LowSpeedTimeSeconds = time,
                }));
        }

        // UserAgent CR/LF 注入（EnsureNoCrLf 的另一调用点，补全 partial 覆盖）
        [Fact]
        public async Task SendAsync_UserAgentWithCrLf_FailsFast()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api)
            {
                UserAgent = "Evil\r\nInjected: 1",
            };

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(
                new HttpRequest { Url = "http://example.invalid/" }));
            Assert.Contains("CR/LF", ex.Message);
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
