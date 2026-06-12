using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Http;
using CurlUnity.Native;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    // HttpResponse 的 native 资源生命周期：
    //   - 漏 Dispose → finalizer 兜底回收 easy handle（泄漏安全网）
    //   - 未释放的 response 通过 CurlGlobal 引用计数压住 curl_global_cleanup，
    //     保证 finalizer 的 curl_easy_cleanup 不会发生在库卸载之后
    [Collection("CurlGlobal")]
    public class HttpResponseLifetimeTests
    {
        private static FakeCurlApi NewCompletingApi(Action<IntPtr> onHandle)
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero)
                {
                    onHandle(handle);
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
                }
            };
            return api;
        }

        // NoInlining/NoOptimization: 让 response 的唯一强引用明确止于本方法。
        // finalizer 语义本身不依赖完整 async 请求链路；async state machine 和
        // coverlet 插桩会延长局部变量生命周期，使 GC 时序断言在 CI 下不稳定。
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static WeakReference CreateLeakedResponse(FakeCurlApi api, out IntPtr handle)
        {
            handle = api.EasyInit();
            var resp = new HttpResponse(api, new CurlResponse { EasyHandle = handle });
            Assert.NotNull(resp);
            return new WeakReference(resp); // 故意不 Dispose
        }

        private static bool WaitUntilCleanedUp(FakeCurlApi api, IntPtr handle)
        {
            for (int i = 0; i < 20; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                if (api.GetEasyHandleState(handle).IsCleanedUp)
                    return true;

                Thread.Sleep(25);
            }
            return false;
        }

        [Fact]
        public void Finalizer_ReclaimsLeakedEasyHandle()
        {
            var api = new FakeCurlApi();
            var weak = CreateLeakedResponse(api, out var captured);

            Assert.NotEqual(IntPtr.Zero, captured);
            var cleaned = WaitUntilCleanedUp(api, captured);
            if (!cleaned && weak.Target is IDisposable leaked)
                leaked.Dispose(); // 失败路径兜底释放 CurlGlobal refcount, 避免污染后续测试

            Assert.True(cleaned,
                "未 Dispose 的 HttpResponse 应由 finalizer 兜底回收 easy handle");
        }

        [Fact]
        public async Task ConcurrentGetInfoAndDispose_NeverTouchesFreedHandle()
        {
            IntPtr captured = IntPtr.Zero;
            var api = NewCompletingApi(h => captured = h);
            using var client = new CurlHttpClient(api);

            var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                .WaitAsync(TimeSpan.FromSeconds(5));

            // 多线程读惰性属性，同时并发 Dispose——锁保证 getinfo 与 cleanup 互斥，
            // Dispose 后读属性应拿到安全默认值而不是踩已释放 handle / 抛异常
            var readers = new Task[4];
            using var start = new System.Threading.ManualResetEventSlim(false);
            for (int i = 0; i < readers.Length; i++)
            {
                readers[i] = Task.Run(() =>
                {
                    start.Wait();
                    for (int n = 0; n < 1000; n++)
                    {
                        _ = resp.ContentType;
                        _ = resp.ContentLength;
                        _ = resp.EffectiveUrl;
                    }
                });
            }
            var disposer = Task.Run(() => { start.Wait(); resp.Dispose(); });

            start.Set();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
            await disposer;

            Assert.True(resp.IsDisposed);
            Assert.Null(resp.ContentType);
            Assert.Equal(-1, resp.ContentLength);
        }

        [Fact]
        public async Task UndisposedResponse_KeepsCurlGlobalAlive_UntilDisposed()
        {
            IntPtr captured = IntPtr.Zero;
            var api = NewCompletingApi(h => captured = h);

            var client = new CurlHttpClient(api);
            IHttpResponse resp = null;
            try
            {
                resp = await client
                    .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                    .WaitAsync(TimeSpan.FromSeconds(5));

                client.Dispose();
                // response 仍活着 → 引用计数 > 0 → 不允许 curl_global_cleanup
                Assert.Equal(0, api.CurlGlobalCleanupCalls);

                resp.Dispose();
                resp = null;
                Assert.Equal(1, api.CurlGlobalCleanupCalls);
                Assert.True(api.GetEasyHandleState(captured).IsCleanedUp);
            }
            finally
            {
                resp?.Dispose();
                client.Dispose();
            }
        }
    }
}
