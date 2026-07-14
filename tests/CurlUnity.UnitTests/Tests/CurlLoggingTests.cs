using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Diagnostics;
using CurlUnity.Http;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    [Collection("CurlGlobal")]
    public class CurlLoggingTests
    {
        [Fact]
        public void Logger_NullOptions_UsesWarningLevel()
        {
            var logger = new CurlLogger(null);

            Assert.True(logger.IsEnabled(CurlLogLevel.Error));
            Assert.True(logger.IsEnabled(CurlLogLevel.Warning));
            Assert.False(logger.IsEnabled(CurlLogLevel.Verbose));
        }

        [Theory]
        [InlineData(CurlLogLevel.Off, 0)]
        [InlineData(CurlLogLevel.Error, 1)]
        [InlineData(CurlLogLevel.Warning, 2)]
        [InlineData(CurlLogLevel.Verbose, 3)]
        public void Logger_FiltersEntriesByConfiguredLevel(CurlLogLevel configuredLevel,
            int expectedCount)
        {
            var sink = new CollectingSink();
            var logger = new CurlLogger(new CurlLogOptions
            {
                Level = configuredLevel,
                Sink = sink,
            });

            logger.Error(CurlLogCategory.Core, "error");
            logger.Warning(CurlLogCategory.Http, "warning");
            logger.Verbose(CurlLogCategory.Sse, "verbose");

            Assert.Equal(expectedCount, sink.Entries.Count);
        }

        [Fact]
        public void Logger_SnapshotsOptions()
        {
            var first = new CollectingSink();
            var second = new CollectingSink();
            var options = new CurlLogOptions
            {
                Level = CurlLogLevel.Warning,
                Sink = first,
            };
            var logger = new CurlLogger(options);

            options.Level = CurlLogLevel.Verbose;
            options.Sink = second;
            logger.Verbose(CurlLogCategory.Http, "hidden");
            logger.Warning(CurlLogCategory.Core, "shown");

            var entry = Assert.Single(first.Entries);
            Assert.Equal(CurlLogLevel.Warning, entry.Level);
            Assert.Empty(second.Entries);
        }

        [Fact]
        public void Logger_PreservesStructuredEntryFields()
        {
            var before = DateTimeOffset.UtcNow;
            var sink = new CollectingSink();
            var logger = new CurlLogger(new CurlLogOptions
            {
                Level = CurlLogLevel.Verbose,
                Sink = sink,
            });
            var exception = new InvalidOperationException("boom");

            logger.Error(CurlLogCategory.Certificates, "failed", exception, 42);

            var entry = Assert.Single(sink.Entries);
            Assert.InRange(entry.TimestampUtc, before, DateTimeOffset.UtcNow);
            Assert.Equal(CurlLogLevel.Error, entry.Level);
            Assert.Equal(CurlLogCategory.Certificates, entry.Category);
            Assert.Equal("failed", entry.Message);
            Assert.Same(exception, entry.Exception);
            Assert.Equal(42, entry.RequestId);
        }

        [Fact]
        public void Logger_SwallowsSinkExceptions()
        {
            var logger = new CurlLogger(new CurlLogOptions
            {
                Level = CurlLogLevel.Error,
                Sink = new ThrowingSink(),
            });

            var exception = Record.Exception(
                () => logger.Error(CurlLogCategory.Core, "must not escape"));

            Assert.Null(exception);
        }

        [Theory]
        [InlineData(CurlLogLevel.Warning, false)]
        [InlineData(CurlLogLevel.Verbose, true)]
        public void Client_LogLevelControlsNativeVerbose(CurlLogLevel level,
            bool expectedEnabled)
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api, logOptions: new CurlLogOptions
            {
                Level = level,
                Sink = new CollectingSink(),
            });

            _ = client.SendAsync(new HttpRequest { Url = "http://example.invalid/" });

            var options = api.GetEasyHandleState(api.LastEasyHandle).LongOptions;
            Assert.Equal(expectedEnabled, options.ContainsKey(41)); // CURLOPT_VERBOSE
        }

        [Fact]
        public async Task Client_NativeVerboseSetupFailure_OnlyLogsWarning()
        {
            var api = new FakeCurlApi
            {
                SetOptLongHook = (_, option, _) => option == 41 ? 7 : null,
            };
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, 0);
            };
            var sink = new CollectingSink();
            using var client = new CurlHttpClient(api, logOptions: new CurlLogOptions
            {
                Level = CurlLogLevel.Verbose,
                Sink = sink,
            });

            using var response = await client.SendAsync(new HttpRequest
            {
                Url = "http://example.invalid/",
            }).WaitAsync(TimeSpan.FromSeconds(2));

            var warning = Assert.Single(sink.Entries,
                entry => entry.Level == CurlLogLevel.Warning);
            Assert.Contains("CURLOPT_VERBOSE", warning.Message);
        }

        private sealed class CollectingSink : ICurlLogSink
        {
            public List<CurlLogEntry> Entries { get; } = new List<CurlLogEntry>();

            public void Write(CurlLogEntry entry) => Entries.Add(entry);
        }

        private sealed class ThrowingSink : ICurlLogSink
        {
            public void Write(CurlLogEntry entry) => throw new InvalidOperationException("sink failed");
        }
    }
}
