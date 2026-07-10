using System;
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
    }
}
