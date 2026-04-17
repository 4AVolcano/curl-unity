using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Http;
using CurlUnity.IntegrationTests.Fixtures;
using CurlUnity.Native;
using Xunit;

namespace CurlUnity.IntegrationTests.Tests
{
    [Collection("Integration")]
    public class ReproTests : IDisposable
    {
        private readonly TestServerFixture _server;

        public ReproTests(TestServerFixture server, CurlGlobalFixture _)
        {
            _server = server;
        }

        public void Dispose() { }

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
                WaitUntil(() => GetPrivateBool(request, "_disposed"), TimeSpan.FromSeconds(1)),
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
