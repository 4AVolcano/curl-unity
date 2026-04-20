using System;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using CurlUnity.Native;
using Xunit;
using Xunit.Abstractions;

// CurlRequestState / CurlRequest.State are internal — accessible from the test
// assembly because the source is compiled in via the csproj's Compile glob.

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class ReproTests : IDisposable
    {
        private readonly TestServerFixture _server;
        private readonly ITestOutputHelper _output;

        public ReproTests(TestServerFixture server, CurlGlobalFixture _, ITestOutputHelper output)
        {
            _server = server;
            _output = output;
        }

        public void Dispose() { }

        // =====================================================================
        // 回归保护：libcurl 在 FOLLOWLOCATION=1 下不把中间 3xx 响应的 body
        // 投递给 write callback（我们实证验证过的行为）。
        // =====================================================================
        // curl 文档没有明文承诺这件事，但实测和源码都表明它是这样。Commit 4
        // 的状态行检测逻辑**没有**针对这种情况加防御——前提是 libcurl 维持当前
        // 行为。一旦将来 libcurl 改了（比如某个 opt 组合导致中间 body 也被
        // 写入），下面两个测试会立刻失败，提醒我们重新加防御。
        //
        // 端点 /redirect-with-body/{n}：
        //   n > 0  → 302 + Location + body="intermediate-body-{n}"
        //   n == 0 → 200 + body="final-body"

        [Fact]
        [Trait("Category", "Regression")]
        public async Task RedirectWithBody_BufferedMode_OnlyDeliversFinalBody()
        {
            using var client = new CurlHttpClient();
            client.PreferredVersion = HttpVersion.Default;

            var req = new HttpRequest { Url = $"{_server.HttpUrl}/redirect-with-body/2" };
            using var resp = await client.SendAsync(req);

            Assert.True(resp.HasResponse, $"err={resp.ErrorCode} msg={resp.ErrorMessage}");
            Assert.Equal(200, resp.StatusCode);

            var body = Encoding.UTF8.GetString(resp.Body);
            _output.WriteLine($"Buffered body (len={body.Length}): <<<{body}>>>");

            // 如果 curl 行为改变、把中间 body 也送到 write callback，body 会
            // 包含 "intermediate-body-2" / "intermediate-body-1"，断言就会 fail。
            Assert.Equal("final-body", body);
        }

        [Fact]
        [Trait("Category", "Regression")]
        public async Task RedirectWithBody_StreamingMode_OnlyDeliversFinalBody()
        {
            using var client = new CurlHttpClient();
            client.PreferredVersion = HttpVersion.Default;

            var received = new StringBuilder();
            var chunkSizes = new System.Collections.Generic.List<int>();
            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/redirect-with-body/2",
                OnDataReceived = (buf, offset, len) =>
                {
                    chunkSizes.Add(len);
                    received.Append(Encoding.UTF8.GetString(buf, offset, len));
                },
            };
            using var resp = await client.SendAsync(req);

            Assert.True(resp.HasResponse, $"err={resp.ErrorCode} msg={resp.ErrorMessage}");
            Assert.Equal(200, resp.StatusCode);

            var delivered = received.ToString();
            _output.WriteLine($"Streamed chunks: {string.Join(",", chunkSizes)}");
            _output.WriteLine($"Streamed total (len={delivered.Length}): <<<{delivered}>>>");

            // 最严格的断言：DataCallback 只拿到最终 body，没有中间 body 泄漏。
            Assert.Equal("final-body", delivered);
            Assert.DoesNotContain("intermediate", delivered);
        }


        [Fact]
        [Trait("Category", "Repro")]
        public void CancelBeforeSend_ShouldNotLeaveDisposedRequestActiveInMulti()
        {
            using var worker = new CurlBackgroundWorker
            {
                PollTimeoutMs = 10
            };
            worker.Start();

            using var request = new CurlRequest();
            CurlNative.curl_unity_setopt_string(request.Handle, CurlNative.CURLOPT_URL, $"{_server.HttpUrl}/delay/1000");
            CurlNative.curl_unity_setopt_long(request.Handle, CurlNative.CURLOPT_NOSIGNAL, 1);

            var completed = new TaskCompletionSource<CurlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            request.OnComplete = resp => completed.TrySetResult(resp);

            worker.Cancel(request);

            Assert.True(
                WaitUntil(() => request.State == CurlRequestState.Disposed, TimeSpan.FromSeconds(1)),
                "Cancel should be able to dispose the request before it is submitted.");

            worker.Send(request);
            Thread.Sleep(200);

            Assert.Equal(
                0,
                GetActiveRequestCount(worker));
            Assert.True(
                completed.Task.IsCompleted,
                "A request disposed before send should not stay pending forever after Send.");
        }

        [Fact]
        [Trait("Category", "Repro")]
        public async Task RedirectHeaders_ShouldOnlyExposeFinalHopHeaders()
        {
            using var client = new CurlHttpClient();
            client.PreferredVersion = HttpVersion.Default;

            var req = new HttpRequest
            {
                Url = $"{_server.HttpUrl}/redirect-with-headers/2",
                EnableResponseHeaders = true,
            };

            using var resp = await client.SendAsync(req);

            Assert.True(resp.HasResponse);
            Assert.Equal(200, resp.StatusCode);
            Assert.NotNull(resp.Headers);
            Assert.False(resp.Headers.ContainsKey("location"));
            Assert.False(resp.Headers.ContainsKey("x-intermediate-hop"));
            Assert.True(resp.Headers.ContainsKey("x-final-hop"));
            Assert.Equal("0", resp.Headers["x-final-hop"][0]);
        }

        private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                    return true;
                Thread.Sleep(10);
            }

            return predicate();
        }

        private static bool GetPrivateBool(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.GetValue(target) is bool value && value;
        }

        private static int GetActiveRequestCount(CurlBackgroundWorker worker)
        {
            var multiField = typeof(CurlBackgroundWorker).GetField("_multi", BindingFlags.Instance | BindingFlags.NonPublic);
            var multi = multiField?.GetValue(worker);
            if (multi == null)
                return -1;

            var activeRequestsField = multi.GetType().GetField("_activeRequests", BindingFlags.Instance | BindingFlags.NonPublic);
            var activeRequests = activeRequestsField?.GetValue(multi);
            if (activeRequests == null)
                return -1;

            var countProperty = activeRequests.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            if (countProperty?.GetValue(activeRequests) is int count)
                return count;

            return -1;
        }
    }
}
