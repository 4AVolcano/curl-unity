using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Http;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    public class ReproTests
    {
        [Fact]
        [Trait("Category", "Repro")]
        public void Constructor_ShouldFail_WhenGlobalInitFails()
        {
            var api = new FakeCurlApi
            {
                CurlGlobalInitResult = 7
            };

            CurlHttpClient client = null;
            try
            {
                var ex = Record.Exception(() => client = new CurlHttpClient(api));
                Assert.IsType<InvalidOperationException>(ex);
            }
            finally
            {
                client?.Dispose();
            }
        }

        [Fact]
        [Trait("Category", "Repro")]
        public async Task SendAsync_ShouldFailFast_WhenMultiAddHandleFails()
        {
            var api = new FakeCurlApi
            {
                MultiAddHandleResult = 9
            };

            using var client = new CurlHttpClient(api);

            var task = client.GetAsync("http://example.invalid/");
            var completed = await Task.WhenAny(task, Task.Delay(300));
            Assert.Same(task, completed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            // `using` 自动 Dispose，无需显式再调一次；fault 后的 task 也不需要再 await。
        }

        [Fact]
        [Trait("Category", "Repro")]
        public async Task BackgroundWorker_Dispose_CompletesPromptlyAndCleansUp_WhenPollHonorsTimeout()
        {
            // 真实 libcurl 的 curl_multi_poll 会严格遵守其 timeoutMs 参数（由
            // 底层 poll(2)/WSAPoll 保证），并且被 Wakeup 时立即返回。
            // FakeCurlApi 默认 MultiPoll 行为就是这个语义。
            //
            // 我们期望 Dispose 能很快完成、且把 multi handle 正常 cleanup。
            var api = new FakeCurlApi();
            var worker = new CurlBackgroundWorker(api)
            {
                PollTimeoutMs = 1000,
            };

            worker.Start();
            // 让 worker 线程至少进入一次 Poll
            await Task.Delay(50);

            var sw = Stopwatch.StartNew();
            worker.Dispose();
            sw.Stop();

            // Wakeup 让当前 poll 立即返回，线程随即退出；远小于 PollTimeoutMs。
            Assert.True(
                sw.ElapsedMilliseconds < 500,
                $"Dispose should return promptly when wakeup is honored, took {sw.ElapsedMilliseconds}ms");
            Assert.True(
                api.MultiCleanupCalls > 0,
                "Multi handle should be cleaned up after normal shutdown.");
        }

        [Fact]
        [Trait("Category", "Repro")]
        public async Task BackgroundWorker_Dispose_SkipsCleanupAndReturnsInBoundedTime_WhenUserCallbackBlocks()
        {
            // 场景：用户注册的 DataCallback 在 libcurl 的 write 回调中阻塞。
            // worker 线程卡在 curl_multi_perform 里，Wakeup 无效，Join 超时。
            //
            // 期望行为：
            //   - Dispose 在 (PollTimeoutMs * 2 + 500) + 少量 buffer 之内返回，
            //     不会永远等。
            //   - 跳过 curl_multi_cleanup（否则与仍在执行回调的线程竞争同一
            //     handle，产生 use-after-free）。
            const int pollTimeoutMs = 200;
            var joinTimeoutMs = pollTimeoutMs * 2 + 500; // 900ms, = Worker.Dispose 里的上限
            var testTimeoutMs = joinTimeoutMs + 1000;    // 给点裕量

            var api = new FakeCurlApi();
            using var callbackEntered = new ManualResetEventSlim(false);
            using var releaseCallback = new ManualResetEventSlim(false);
            var callbackInvoked = 0;

            api.OnMultiPerform = multi =>
            {
                var handle = api.GetFirstActiveHandle(multi);
                if (handle == IntPtr.Zero) return;
                if (Interlocked.Exchange(ref callbackInvoked, 1) != 0) return;
                api.InvokeWriteCallback(handle, new byte[] { 1, 2, 3 });
            };

            // 用 `using var` 保证即使断言在 Wait 阶段就 fail，worker/request 也能被
            // 清理——否则 worker 后台线程会留在进程里，污染后续测试状态。
            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = pollTimeoutMs };
            using var request = new CurlRequest(api)
            {
                DataCallback = (_, _, _) =>
                {
                    callbackEntered.Set();
                    releaseCallback.Wait();
                },
            };

            worker.Start();
            worker.Send(request);

            Task disposeTask = null;
            try
            {
                Assert.True(
                    callbackEntered.Wait(TimeSpan.FromSeconds(1)),
                    "Streaming callback was not invoked.");

                var sw = Stopwatch.StartNew();
                disposeTask = Task.Run(() => worker.Dispose());

                var completed = await Task.WhenAny(disposeTask, Task.Delay(testTimeoutMs));
                sw.Stop();

                Assert.Same(disposeTask, completed);
                Assert.True(
                    sw.ElapsedMilliseconds < testTimeoutMs,
                    $"Dispose should return within bounded time even when callback is stuck; took {sw.ElapsedMilliseconds}ms");
                // Join 超时的下限：应该至少等了 joinTimeout 那么久（减一点抖动容差）
                Assert.True(
                    sw.ElapsedMilliseconds >= joinTimeoutMs - 100,
                    $"Dispose should wait close to the join timeout before giving up; took only {sw.ElapsedMilliseconds}ms");

                // 关键断言：cleanup 被跳过（避免 UAF）
                Assert.Equal(0, api.MultiCleanupCalls);
            }
            finally
            {
                // 解除用户回调阻塞。无论测试走到哪一步，这里一定要 Set：如果还没
                // Set 就进入 `using var` 的 worker.Dispose()，worker 会因回调仍
                // 挂住而吃满 joinTimeout，拖慢测试套件。
                releaseCallback.Set();
                if (disposeTask != null)
                    await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
