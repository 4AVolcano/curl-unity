using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Diagnostics;
using CurlUnity.Http;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    [Collection("CurlGlobal")]
    public class HttpLoggingTests
    {
        [Fact]
        public async Task WarningLevel_DoesNotEmitRequestLifecycle()
        {
            var api = CreateCompletingApi(0, 200);
            var sink = new CollectingSink();
            using var client = CreateClient(api, CurlLogLevel.Warning, sink);

            using var response = await client.SendAsync(new HttpRequest
            {
                Url = "https://example.com/path",
            }).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Empty(sink.Entries.FindAll(
                entry => entry.Category == CurlLogCategory.Http));
        }

        [Fact]
        public async Task VerboseLifecycle_RedactsUrlAndCorrelatesCompletion()
        {
            var api = CreateCompletingApi(0, 200);
            var sink = new CollectingSink();
            using var client = CreateClient(api, CurlLogLevel.Verbose, sink);

            using var response = await client.SendAsync(new HttpRequest
            {
                Method = HttpMethod.Get,
                Url = "https://user:password@example.com/path?token=secret",
            }).WaitAsync(TimeSpan.FromSeconds(2));

            var entries = sink.Entries.FindAll(
                entry => entry.Category == CurlLogCategory.Http);
            Assert.Equal(2, entries.Count);
            Assert.Equal(entries[0].RequestId, entries[1].RequestId);
            Assert.NotNull(entries[0].RequestId);
            Assert.Contains("GET https://example.com/path?<redacted> started", entries[0].Message);
            Assert.DoesNotContain("user", entries[0].Message);
            Assert.DoesNotContain("password", entries[0].Message);
            Assert.DoesNotContain("secret", entries[0].Message);
            Assert.Contains("completed status=200 elapsed=", entries[1].Message);
        }

        [Fact]
        public async Task VerboseLifecycle_RecordsCurlFailureAsRequestResult()
        {
            const int curlTimeoutCode = 28;
            var api = CreateCompletingApi(curlTimeoutCode, 0);
            var sink = new CollectingSink();
            using var client = CreateClient(api, CurlLogLevel.Verbose, sink);

            var exception = await Assert.ThrowsAsync<CurlHttpException>(() =>
                client.SendAsync(new HttpRequest
                {
                    Url = "https://example.com/slow",
                }).WaitAsync(TimeSpan.FromSeconds(2)));

            var entries = sink.Entries.FindAll(
                entry => entry.Category == CurlLogCategory.Http);
            Assert.Equal(2, entries.Count);
            Assert.Equal(entries[0].RequestId, entries[1].RequestId);
            Assert.Contains("failed kind=Timeout curlCode=28 elapsed=", entries[1].Message);
            Assert.Same(exception, entries[1].Exception);
            Assert.Equal(CurlLogLevel.Verbose, entries[1].Level);
        }

        [Fact]
        public async Task VerboseLifecycle_RecordsCancellationOnce()
        {
            var api = new FakeCurlApi();
            var sink = new CollectingSink();
            using var client = CreateClient(api, CurlLogLevel.Verbose, sink);
            using var cts = new CancellationTokenSource();

            var task = client.SendAsync(new HttpRequest
            {
                Url = "https://example.com/wait",
            }, cts.Token);
            Assert.True(SpinWait.SpinUntil(
                () => api.GetFirstActiveHandle(api.LastMultiHandle) != IntPtr.Zero, 2_000));

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            var entries = sink.Entries.FindAll(
                entry => entry.Category == CurlLogCategory.Http);
            Assert.Equal(2, entries.Count);
            Assert.Equal(entries[0].RequestId, entries[1].RequestId);
            Assert.Contains("cancelled elapsed=", entries[1].Message);
        }

        [Fact]
        public async Task ClientInstances_DoNotCrossDeliverLifecycleEntries()
        {
            var firstSink = new CollectingSink();
            var secondSink = new CollectingSink();
            using var first = CreateClient(CreateCompletingApi(0, 201),
                CurlLogLevel.Verbose, firstSink);
            using var second = CreateClient(CreateCompletingApi(0, 202),
                CurlLogLevel.Verbose, secondSink);

            using var firstResponse = await first.SendAsync(new HttpRequest
            {
                Url = "https://first.example/path",
            }).WaitAsync(TimeSpan.FromSeconds(2));
            using var secondResponse = await second.SendAsync(new HttpRequest
            {
                Url = "https://second.example/path",
            }).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.All(firstSink.Entries.FindAll(e => e.Category == CurlLogCategory.Http),
                entry => Assert.DoesNotContain("second.example", entry.Message));
            Assert.All(secondSink.Entries.FindAll(e => e.Category == CurlLogCategory.Http),
                entry => Assert.DoesNotContain("first.example", entry.Message));
            Assert.Contains(firstSink.Entries, entry => entry.Message.Contains("first.example"));
            Assert.Contains(secondSink.Entries, entry => entry.Message.Contains("second.example"));
        }

        private static CurlHttpClient CreateClient(FakeCurlApi api, CurlLogLevel level,
            ICurlLogSink sink)
        {
            return new CurlHttpClient(api, logOptions: new CurlLogOptions
            {
                Level = level,
                Sink = sink,
            });
        }

        private static FakeCurlApi CreateCompletingApi(int curlCode, long statusCode)
        {
            var api = new FakeCurlApi();
            var completed = false;
            api.OnMultiPerform = multi =>
            {
                if (completed) return;
                var handle = api.GetFirstActiveHandle(multi);
                if (handle == IntPtr.Zero) return;

                completed = true;
                api.GetEasyHandleState(handle).ResponseCode = statusCode;
                api.EnqueueCompletion(handle, curlCode);
            };
            return api;
        }

        private sealed class CollectingSink : ICurlLogSink
        {
            public List<CurlLogEntry> Entries { get; } = new List<CurlLogEntry>();

            public void Write(CurlLogEntry entry)
            {
                lock (Entries)
                    Entries.Add(entry);
            }
        }
    }
}
