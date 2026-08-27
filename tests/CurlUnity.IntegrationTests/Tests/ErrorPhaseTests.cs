using System;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    /// <summary>
    /// 钉住 <see cref="CurlHttpException.ErrorPhase"/> 的契约:只有"没跟过重定向 + 拿到响应
    /// 状态行"才报 <see cref="HttpErrorPhase.Transfer"/>, 其余一律
    /// <see cref="HttpErrorPhase.Undefined"/>。
    /// </summary>
    [Collection("Integration")]
    public class ErrorPhaseTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly CurlHttpClient _client;

        public ErrorPhaseTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
            _client = new CurlHttpClient
            {
                PreferredVersion = HttpVersion.Default,
                VerifySSL = false, // self-signed cert in TestServerFixture
            };
        }

        public void Dispose() => _client.Dispose();

        // 回归测试:建连失败必须留空。libcurl 8.21 的 mstate_enter_completed 会给
        // from_state < MSTATE_DID 的失败补打 TIMER_STARTTRANSFER, 所以早先那版拿计时里程碑
        // 当判据的实现会在这里错报 Transfer。
        [Fact]
        public async Task ConnectFailure_LeavesPhaseUndefined()
        {
            // Port 1 should not be listening
            var ex = await Assert.ThrowsAsync<CurlHttpException>(
                () => _client.GetAsync("http://localhost:1/nope"));

            Assert.Equal(HttpErrorKind.ConnectFailed, ex.ErrorKind);
            Assert.Equal(HttpErrorPhase.Undefined, ex.ErrorPhase);
        }

        // 服务端在 timeout 之前一个字节都不发 → 定位不到具体环节, 留 Undefined。
        [Fact]
        public async Task Timeout_BeforeAnyResponse_LeavesPhaseUndefined()
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

        // 回归测试:跟过重定向就必须留空。info.httpcode 存在于 easy handle 而非每一跳, 上一跳的
        // 状态码会留到下一跳; 这里第一跳的 302 会一直挂在那儿, 若没有 redirect_count 守卫,
        // 即使下一跳压根没拿到状态行也会错报 Transfer。
        // 本例第二跳其实拿到了状态行(headers 已 flush), 结论仍是 Undefined —— 宁可少报不错报。
        [Fact]
        public async Task Timeout_AfterRedirect_LeavesPhaseUndefined()
        {
            var target = Uri.EscapeDataString("/sse-silent-headers?silentMs=10000");
            var ex = await Assert.ThrowsAsync<CurlHttpException>(async () =>
            {
                var req = new HttpRequest
                {
                    Url = $"{_server.HttpsUrl}/redirect-to?to={target}",
                    TimeoutMs = 1500,
                };
                using var resp = await _client.SendAsync(req);
            });

            Assert.Equal(HttpErrorKind.Timeout, ex.ErrorKind);
            Assert.Equal(HttpErrorPhase.Undefined, ex.ErrorPhase);
        }

        // 响应头已 flush、随后静默到超时 → 状态行已到手, 确定在传输中。
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
