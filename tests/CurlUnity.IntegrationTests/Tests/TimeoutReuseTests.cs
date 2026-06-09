using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    /// <summary>
    /// 验证连接复用路径下 TimeoutMs 仍然严格 enforce。
    ///
    /// 背景: 业务侧观察到一个现象——先在正常网络下访问某 host 建立连接,然后启用
    /// 弱网模拟(高延迟 / 丢包),再发请求时实际耗时远超配置的 TimeoutMs(典型 16-21s
    /// vs. 配置 5-10s)。已经把怀疑点收敛到"连接复用绕过了 timeout enforce"。
    ///
    /// 这里用 TestServer 的 /delay/N 端点最小复现该场景:第一次 GET /hello 让 libcurl
    /// 把 TCP/TLS 连接放进 multi 的 connection cache;第二次请求 /delay/N 走复用路径
    /// 但 server 故意慢响应。如果 TimeoutMs 在复用路径下仍 enforce,本测试 PASS。
    /// </summary>
    [Collection("Integration")]
    public class TimeoutReuseTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public TimeoutReuseTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient
            {
                PreferredVersion = HttpVersion.Default,
                VerifySSL = false, // self-signed cert in TestServerFixture
            };
        }

        public void Dispose() => _client.Dispose();

        // 第一次建立 keep-alive 连接,第二次复用 + 慢响应 + 短 timeout。
        // 期望: timeout 在 ~1500ms 触发,而不是等到 server delay 的 10s 才回。
        // 跑两次:HTTP/1.1 (明文) 和 HTTPS (HTTP/2 via ALPN)。
        [Theory]
        [InlineData(false)] // HTTP/1.1
        [InlineData(true)]  // HTTPS,Kestrel 走 HTTP/2 ALPN 协商
        public async Task Timeout_OnReusedConnection_StillFires(bool https)
        {
            var baseUrl = https ? _server.HttpsUrl : _server.HttpUrl;

            // Step 1: 第一次 GET 让连接进 cache
            using (var resp1 = await _client.GetAsync($"{baseUrl}/hello"))
            {
                Assert.Equal(200, resp1.StatusCode);
            }

            // Step 2: 同一 client / 同一 origin,server 故意慢响应 10s,client 设 1.5s timeout。
            // 如果连接复用 + timer 正常,应该 ~1.5s 抛 Timeout。
            // 如果 reuse 路径下 timer 失效,会等满 10s 然后成功(或抛别的异常)。
            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<CurlHttpException>(async () =>
            {
                var req = new HttpRequest
                {
                    Url = $"{baseUrl}/delay/10000",
                    TimeoutMs = 1500,
                };
                using var resp = await _client.SendAsync(req);
            });
            sw.Stop();

            Assert.Equal(HttpErrorKind.Timeout, ex.ErrorKind);
            Assert.Equal(28, ex.CurlCode); // CURLE_OPERATION_TIMEDOUT

            // 关键 assertion: 实际耗时必须接近 TimeoutMs,而不是 server 的 delay 值。
            // 上限放宽到 4000ms 以容忍 worker poll(1s) 的检查粒度 + CI 抖动。
            // 如果 reuse 路径下 timer 失效,这里会看到 ~10000ms 而 FAIL。
            Assert.InRange(sw.ElapsedMilliseconds, 1000, 4000);
        }

        // 对照组: 不预热,直接发 delay 请求。connect 阶段是新建连接(无复用),
        // 已知 ErrorHandlingTests.Timeout_Throws_TimeoutKind 类似场景能 PASS。
        // 加这个测试是为了在出错时帮判定"是 reuse 路径独有的问题,还是 timeout 整体失效"。
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Timeout_OnFreshConnection_StillFires(bool https)
        {
            var baseUrl = https ? _server.HttpsUrl : _server.HttpUrl;

            using var freshClient = new CurlHttpClient
            {
                PreferredVersion = HttpVersion.Default,
                VerifySSL = false,
            };

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<CurlHttpException>(async () =>
            {
                var req = new HttpRequest
                {
                    Url = $"{baseUrl}/delay/10000",
                    TimeoutMs = 1500,
                };
                using var resp = await freshClient.SendAsync(req);
            });
            sw.Stop();

            Assert.Equal(HttpErrorKind.Timeout, ex.ErrorKind);
            Assert.InRange(sw.ElapsedMilliseconds, 1000, 4000);
        }
    }
}
