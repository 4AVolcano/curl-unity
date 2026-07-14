using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Diagnostics;
using CurlUnity.Http;
using CurlUnity.Native;
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
            var logger = new CurlLogger(new CurlLogOptions(configuredLevel, sink));

            logger.Error(CurlLogCategory.Core, "error");
            logger.Warning(CurlLogCategory.Http, "warning");
            logger.Verbose(CurlLogCategory.Sse, "verbose");

            Assert.Equal(expectedCount, sink.Entries.Count);
        }

        [Fact]
        public void Logger_UsesImmutableOptions()
        {
            var first = new CollectingSink();
            var options = new CurlLogOptions(CurlLogLevel.Warning, first);
            var logger = new CurlLogger(options);

            logger.Verbose(CurlLogCategory.Http, "hidden");
            logger.Warning(CurlLogCategory.Core, "shown");

            var entry = Assert.Single(first.Entries);
            Assert.Equal(CurlLogLevel.Warning, entry.Level);
        }

        [Fact]
        public void Logger_PreservesStructuredEntryFields()
        {
            var before = DateTimeOffset.UtcNow;
            var sink = new CollectingSink();
            var logger = new CurlLogger(new CurlLogOptions(CurlLogLevel.Verbose, sink));
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
            var logger = new CurlLogger(new CurlLogOptions(
                CurlLogLevel.Error, new ThrowingSink()));

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
            using var client = new CurlHttpClient(api, logOptions: new CurlLogOptions(
                level, new CollectingSink()));

            _ = client.SendAsync(new HttpRequest { Url = "http://example.invalid/" });

            var options = api.GetEasyHandleState(api.LastEasyHandle).LongOptions;
            Assert.Equal(expectedEnabled,
                options.ContainsKey(CurlNative.CURLOPT_VERBOSE));
        }

        [Fact]
        public async Task Client_NativeVerboseSetupFailure_OnlyLogsWarning()
        {
            const int setOptFailure = 7;
            var api = new FakeCurlApi
            {
                SetOptLongHook = (_, option, _) =>
                    option == CurlNative.CURLOPT_VERBOSE ? setOptFailure : null,
            };
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, 0);
            };
            var sink = new CollectingSink();
            using var client = new CurlHttpClient(api, logOptions: new CurlLogOptions(
                CurlLogLevel.Verbose, sink));

            using var response = await client.SendAsync(new HttpRequest
            {
                Url = "http://example.invalid/",
            }).WaitAsync(TimeSpan.FromSeconds(2));

            var warning = Assert.Single(sink.Entries,
                entry => entry.Level == CurlLogLevel.Warning);
            Assert.Contains("CURLOPT_VERBOSE", warning.Message);
            Assert.Equal(CurlLogCategory.Http, warning.Category);
            Assert.NotNull(warning.RequestId);
        }

        [Fact]
        public void DefaultSinkFormat_IncludesTimestampUtc()
        {
            var timestamp = new DateTimeOffset(
                2026, 7, 14, 12, 34, 56, TimeSpan.Zero);
            var entry = new CurlLogEntry(timestamp, CurlLogLevel.Warning,
                CurlLogCategory.Core, "message", null, 42);
            var formatter = typeof(DefaultCurlLogSink).GetMethod(
                "FormatMessage", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(formatter);
            var formatted = Assert.IsType<string>(formatter.Invoke(
                null, new object[] { entry }));
            Assert.Contains(timestamp.ToString("O"), formatted);
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
