using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CurlUnity.Native;

namespace CurlUnity.IntegrationTests.Fixtures
{
    public class CurlGlobalFixture : IDisposable
    {
        public CurlGlobalFixture()
        {
            // Ensure the native library can be found in the test output directory
            NativeLibrary.SetDllImportResolver(
                typeof(CurlNative).Assembly,
                ResolveDllImport);

            var result = CurlNative.curl_global_init(CurlNative.CURL_GLOBAL_DEFAULT);
            if (result != CurlNative.CURLE_OK)
                throw new InvalidOperationException($"curl_global_init failed: {result}");
        }

        public void Dispose()
        {
            CurlNative.curl_global_cleanup();
        }

        private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "curl_unity")
                return IntPtr.Zero;

            // 在 xUnit 测试里没有 Unity 宏，CurlNative.LIB 固定为 "curl_unity"。
            // Plugins 下的二进制按平台有不同后缀/前缀，这里把请求名映射到实际文件。
            string fileName;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                fileName = "libcurl_unity.dll";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                fileName = "libcurl_unity.dylib";
            else
                fileName = "libcurl_unity.so";

            // Try loading from the assembly's directory first
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            if (assemblyDir != null)
            {
                var libPath = Path.Combine(assemblyDir, fileName);
                if (File.Exists(libPath) && NativeLibrary.TryLoad(libPath, out var handle))
                    return handle;
            }

            // Fallback: let the OS search path find it
            if (NativeLibrary.TryLoad("libcurl_unity", out var fallback))
                return fallback;

            return IntPtr.Zero;
        }
    }
}
