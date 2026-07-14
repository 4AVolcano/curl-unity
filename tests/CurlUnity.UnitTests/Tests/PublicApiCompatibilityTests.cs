using System;
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
    }
}
