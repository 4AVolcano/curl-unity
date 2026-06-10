using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CurlUnity.Http;
using CurlUnity.Native;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    // Diagnostics 的 per-request timing 表必须是弱引用语义：
    //   - response 活着 → GetTiming 可查
    //   - 调用方丢弃 response（即使没 Dispose）→ 不被 timing 表钉住，可被 GC，
    //     finalizer 安全网得以触发，表条目自动消失 —— 否则开启诊断 = 必然泄漏
    [Collection("CurlGlobal")]
    public class HttpDiagnosticsTimingTableTests
    {
        private static FakeCurlApi NewCompletingApi()
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };
            return api;
        }

        [Fact]
        public async Task GetTiming_WhileResponseAlive_ReturnsRecordedEntry()
        {
            var api = NewCompletingApi();
            using var client = new CurlHttpClient(api, enableDiagnostics: true);

            using var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                .WaitAsync(TimeSpan.FromSeconds(5));

            // fake 的 timing 信息全为 0，但条目应存在（与 GetTiming(无记录) 无法用
            // 值区分，这里至少验证调用路径不抛、快照计数正确）
            _ = client.Diagnostics.GetTiming(resp);
            Assert.Equal(1, client.Diagnostics.GetSnapshot().TotalRequests);
        }

        [Fact]
        public void GetTiming_NullResponse_ReturnsDefaultWithoutThrow()
        {
            var api = new FakeCurlApi();
            using var client = new CurlHttpClient(api, enableDiagnostics: true);
            var timing = client.Diagnostics.GetTiming(null);
            Assert.Equal(default, timing);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> SendAndDropResponse(CurlHttpClient client)
        {
            var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                .WaitAsync(TimeSpan.FromSeconds(5));
            return new WeakReference(resp); // 不 Dispose、不保留强引用
        }

        [Fact]
        public async Task TimingTable_DoesNotPinDroppedResponses()
        {
            var api = NewCompletingApi();
            using var client = new CurlHttpClient(api, enableDiagnostics: true);

            var weak = await SendAndDropResponse(client);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(weak.IsAlive,
                "开启诊断时 response 不应被 timing 表强引用钉住（否则诊断 = 必然泄漏）");
        }
    }
}
