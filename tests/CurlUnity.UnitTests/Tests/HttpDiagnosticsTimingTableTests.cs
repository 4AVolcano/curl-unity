using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Diagnostics;
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

        // NoInlining/NoOptimization: 让 response 的唯一强引用明确止于本方法。
        // 这个测试只验证 Diagnostics 的弱表语义；完整 async 请求链路会把 Task /
        // async state machine / coverlet 插桩的临时引用混进来，导致 CI 下 GC
        // 可达性断言不稳定。
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static WeakReference RecordAndDropResponse(FakeCurlApi api, HttpDiagnostics diagnostics)
        {
            var handle = api.EasyInit();
            var resp = new HttpResponse(api, new CurlResponse { EasyHandle = handle });
            diagnostics.Record(resp);
            return new WeakReference(resp); // 不 Dispose、不保留强引用
        }

        private static bool WaitUntilCollected(WeakReference weak)
        {
            for (int i = 0; i < 20; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                if (!weak.IsAlive)
                    return true;

                Thread.Sleep(25);
            }
            return false;
        }

        [Fact]
        public void TimingTable_DoesNotPinDroppedResponses()
        {
            var api = new FakeCurlApi();
            var diagnostics = new HttpDiagnostics();
            var weak = RecordAndDropResponse(api, diagnostics);

            var collected = WaitUntilCollected(weak);
            if (!collected && weak.Target is IDisposable leaked)
                leaked.Dispose(); // 失败路径兜底释放 CurlGlobal refcount, 避免污染后续测试

            Assert.True(collected,
                "开启诊断时 response 不应被 timing 表强引用钉住（否则诊断 = 必然泄漏）");
        }
    }
}
