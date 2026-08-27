using System;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    /// <summary>
    /// 钉住 <see cref="CurlHttpException.ErrorPhase"/> 的契约:超时时它是唯一能区分
    /// "传输已开始后中断"与"定位不了"的判据。CURLcode 28 本身不带阶段,消费方靠这个
    /// 字段决定是标 Transfer 还是留空。
    /// </summary>
    [Collection("Integration")]
    public class TimeoutErrorPhaseTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public TimeoutErrorPhaseTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient
            {
                PreferredVersion = HttpVersion.Default,
                VerifySSL = false, // self-signed cert in TestServerFixture
            };
        }

        public void Dispose() => _client.Dispose();

        // 服务端在 timeout 之前一个字节都不发 → 定位不到具体环节(可能卡在建连、TLS、
        // 或已连上在等响应),留 Undefined。
        [Fact]
        public async Task Timeout_BeforeAnyResponseByte_LeavesPhaseUndefined()
        {
            var ex = await Assert.ThrowsAsync<CurlHttpException>(async () =>
            {
                var req = new HttpRequest
                {
                    Url = $"{_server.HttpsUrl}/delay/10000",
                    TimeoutMs = 1500,
                };
                using var resp = await _client.SendAsync(req);
            });

            Assert.Equal(HttpErrorKind.Timeout, ex.ErrorKind);
            Assert.Equal(HttpErrorPhase.Undefined, ex.ErrorPhase);
        }

        // 响应头已 flush、随后静默到超时 → 确定在传输中。
        // 响应头就算首字节:libcurl 的 TIMER_STARTTRANSFER 在第一次非 INFO 写出时打点。
        [Fact]
        public async Task Timeout_AfterResponseHeaders_ReportsTransfer()
        {
            var ex = await Assert.ThrowsAsync<CurlHttpException>(async () =>
            {
                var req = new HttpRequest
                {
                    Url = $"{_server.HttpsUrl}/sse-silent-headers?silentMs=10000",
                    TimeoutMs = 1500,
                };
                using var resp = await _client.SendAsync(req);
            });

            Assert.Equal(HttpErrorKind.Timeout, ex.ErrorKind);
            Assert.Equal(HttpErrorPhase.Transfer, ex.ErrorPhase);
        }
    }
}
