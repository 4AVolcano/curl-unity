using System;
using System.Threading;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Native;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    // worker 循环的容错语义：
    //   - 单次/偶发异常 → 吞掉继续跑（不影响后续请求）
    //   - 连续 5 次异常 → faulted：fail 掉所有在飞与排队请求，线程退出，
    //     后续 Send 快速失败 —— 永远不允许"线程静默死亡、Task 永久悬挂"。
    public class CurlBackgroundWorkerTests
    {
        private static CurlRequest NewRequest(FakeCurlApi api, out Task<CurlResponse> completion)
        {
            var tcs = new TaskCompletionSource<CurlResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new CurlRequest(api)
            {
                OnComplete = resp => tcs.TrySetResult(resp)
            };
            completion = tcs.Task;
            return request;
        }

        [Fact]
        public async Task WorkerLoop_TransientExceptions_RecoverAndCompleteRequest()
        {
            var api = new FakeCurlApi();
            int performCalls = 0;
            bool completionEnqueued = false;
            api.OnMultiPerform = multi =>
            {
                var n = Interlocked.Increment(ref performCalls);
                if (n <= 2)
                    throw new InvalidOperationException("transient");

                // 恢复后模拟 libcurl 完成传输（在 worker 线程内入队，无并发问题）
                if (!completionEnqueued)
                {
                    var handle = api.GetFirstActiveHandle(multi);
                    if (handle != IntPtr.Zero)
                    {
                        api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
                        completionEnqueued = true;
                    }
                }
            };

            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = 50 };
            worker.Start();

            var request = NewRequest(api, out var completion);
            worker.Send(request);

            var resp = await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(resp.FailureException);
            Assert.Equal(CurlNative.CURLE_OK, resp.CurlCode);
            Assert.False(worker.IsFaulted);
        }

        [Fact]
        public async Task WorkerLoop_PersistentExceptions_FaultAndFailEverything()
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = _ => throw new InvalidOperationException("injected-root-cause");

            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = 50 };
            worker.Start();

            // 在飞请求：已进 multi，fault 时由 FailAllActive 以失败结束
            var inflight = NewRequest(api, out var inflightCompletion);
            worker.Send(inflight);

            var resp = await inflightCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(resp.FailureException);
            Assert.Contains("faulted", resp.FailureException.Message);
            Assert.Equal("injected-root-cause", resp.FailureException.InnerException?.Message);
            Assert.True(worker.IsFaulted);

            // faulted 后的新请求：不入队，立即以失败结束（而不是永久悬挂）
            var late = NewRequest(api, out var lateCompletion);
            worker.Send(late);

            var lateResp = await lateCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(lateResp.FailureException);
            Assert.Contains("faulted", lateResp.FailureException.Message);
        }
    }
}
