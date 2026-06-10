using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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

        // NoInlining: 防止 JIT 把 response 局部变量的生命周期延长到调用方栈帧，
        // 否则 GC.Collect 后对象仍可达，finalizer 不会触发。
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task LeakOneResponse(CurlHttpClient client)
        {
            var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(resp);
            // 故意不 Dispose
        }

        [Fact]
        public async Task Finalizer_ReclaimsLeakedEasyHandle()
        {
            IntPtr captured = IntPtr.Zero;
            var api = NewCompletingApi(h => captured = h);

            using (var client = new CurlHttpClient(api))
            {
                await LeakOneResponse(client);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Assert.NotEqual(IntPtr.Zero, captured);
                Assert.True(api.GetEasyHandleState(captured).IsCleanedUp,
                    "未 Dispose 的 HttpResponse 应由 finalizer 兜底回收 easy handle");
            }
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
            var resp = await client
                .SendAsync(new HttpRequest { Url = "http://example.invalid/" })
                .WaitAsync(TimeSpan.FromSeconds(5));

            client.Dispose();
            // response 仍活着 → 引用计数 > 0 → 不允许 curl_global_cleanup
            Assert.Equal(0, api.CurlGlobalCleanupCalls);

            resp.Dispose();
            Assert.Equal(1, api.CurlGlobalCleanupCalls);
            Assert.True(api.GetEasyHandleState(captured).IsCleanedUp);
        }
    }
}
