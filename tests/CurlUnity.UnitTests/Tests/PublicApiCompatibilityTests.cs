using System;
using System.Linq;
using System.Reflection;
using CurlUnity.Diagnostics;
using CurlUnity.Http;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    public class PublicApiCompatibilityTests
    {
        [Fact]
        public void CurlHttpClient_LegacyBoolConstructor_RemainsAvailable()
        {
            var constructor = typeof(CurlHttpClient).GetConstructor(new[] { typeof(bool) });

            Assert.NotNull(constructor);
        }

        [Fact]
        public void CurlHttpClient_ConnectionLimitConstructor_RemainsAvailable()
        {
            var constructor = typeof(CurlHttpClient).GetConstructor(
                new[] { typeof(int), typeof(int), typeof(bool) });

            Assert.NotNull(constructor);
        }

        [Fact]
        public void CurlHttpClient_LoggingConstructors_AreAvailable()
        {
            Assert.NotNull(typeof(CurlHttpClient).GetConstructor(
                new[] { typeof(CurlLogOptions), typeof(bool) }));
            Assert.NotNull(typeof(CurlHttpClient).GetConstructor(
                new[] { typeof(CurlLogOptions), typeof(int), typeof(int), typeof(bool) }));
        }

        [Fact]
        public void CurlHttpClient_LegacyVerboseProperty_IsRemoved()
        {
            Assert.Null(typeof(CurlHttpClient).GetProperty("Verbose"));
        }

        [Fact]
        public void LoggingContract_HasStablePublicShape()
        {
            Assert.Equal(new[] { "Off", "Error", "Warning", "Verbose" },
                Enum.GetNames(typeof(CurlLogLevel)));
            Assert.Equal(new[] { 0, 1, 2, 3 },
                Enum.GetValues(typeof(CurlLogLevel)).Cast<CurlLogLevel>().Select(v => (int)v));

            var entryProperties = typeof(CurlLogEntry)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .ToDictionary(property => property.Name, property => property.PropertyType);
            Assert.Equal(6, entryProperties.Count);
            Assert.Equal(typeof(DateTimeOffset), entryProperties[nameof(CurlLogEntry.TimestampUtc)]);
            Assert.Equal(typeof(CurlLogLevel), entryProperties[nameof(CurlLogEntry.Level)]);
            Assert.Equal(typeof(CurlLogCategory), entryProperties[nameof(CurlLogEntry.Category)]);
            Assert.Equal(typeof(string), entryProperties[nameof(CurlLogEntry.Message)]);
            Assert.Equal(typeof(Exception), entryProperties[nameof(CurlLogEntry.Exception)]);
            Assert.Equal(typeof(long?), entryProperties[nameof(CurlLogEntry.RequestId)]);

            var sinkMethod = Assert.Single(typeof(ICurlLogSink).GetMethods());
            Assert.Equal(nameof(ICurlLogSink.Write), sinkMethod.Name);
            Assert.Equal(typeof(void), sinkMethod.ReturnType);
            Assert.Equal(typeof(CurlLogEntry),
                Assert.Single(sinkMethod.GetParameters()).ParameterType);

            var defaults = new CurlLogOptions();
            Assert.Equal(CurlLogLevel.Warning, defaults.Level);
            Assert.Null(defaults.Sink);
            Assert.True(typeof(CurlLogOptions).GetProperty(nameof(CurlLogOptions.Level)).CanWrite);
            Assert.True(typeof(CurlLogOptions).GetProperty(nameof(CurlLogOptions.Sink)).CanWrite);
        }
    }
}
