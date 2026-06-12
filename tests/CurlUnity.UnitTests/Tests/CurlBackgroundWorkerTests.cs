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

        // ---------------------------------------------------------------
        // DrainPendingAsFaulted：faulted 时队列里未提交的请求也要被 fail 掉
        // ---------------------------------------------------------------
        [Fact]
        public async Task Fault_DrainsPendingQueueRequests_NotJustInflight()
        {
            var api = new FakeCurlApi();
            var blockPoll = new ManualResetEventSlim(false);

            // Poll 阻塞 → worker 卡在 Poll，无法消费 pendingRequests；
            // 同时 Perform 持续抛异常触发 faulted 流程
            int performCalls = 0;
            api.OnMultiPerform = _ =>
            {
                if (Interlocked.Increment(ref performCalls) >= 1)
                    throw new InvalidOperationException("force-fault");
            };
            api.OnMultiPoll = (_, _) => blockPoll.Wait(50); // 短超时让循环快速迭代

            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = 50 };
            worker.Start();

            // 先等 worker 进入 faulted（等在飞请求完成即可说明 faulted 已触发）
            var sentinel = NewRequest(api, out var sentinelComp);
            worker.Send(sentinel);
            await sentinelComp.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.IsFaulted);

            // 此时 faulted 后再入队的请求应通过 DrainPendingAsFaulted 立即失败
            var queued = NewRequest(api, out var queuedComp);
            worker.Send(queued); // 走 Send 里的第二个 IsFaulted 检查 → DrainPendingAsFaulted

            var queuedResp = await queuedComp.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(queuedResp.FailureException);
            Assert.Contains("faulted", queuedResp.FailureException.Message);
        }

        // ---------------------------------------------------------------
        // FailRequest：OnComplete 回调抛异常时不再向外传播（二重保护）
        // ---------------------------------------------------------------
        [Fact]
        public async Task Fault_OnCompleteThrows_ExceptionIsSwallowed()
        {
            var api = new FakeCurlApi();
            api.OnMultiPerform = _ => throw new InvalidOperationException("force-fault");

            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = 50 };
            worker.Start();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new CurlRequest(api)
            {
                // 回调故意抛异常；FailRequest 里的 catch 应把它吞掉，
                // worker 线程不会因为用户回调抛异常而崩掉
                OnComplete = _ =>
                {
                    tcs.TrySetResult(true);
                    throw new InvalidOperationException("callback-throws");
                }
            };
            worker.Send(request);

            // 能拿到回调说明 OnComplete 被调用了；worker 也不应崩溃（线程自然退出）
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.IsFaulted);
        }

        // ---------------------------------------------------------------
        // consecutiveFailures 临界点：第 4 次异常不进 faulted，第 5 次才进
        // ---------------------------------------------------------------
        [Fact]
        public async Task WorkerLoop_FourConsecutiveExceptions_DoesNotFaultYet()
        {
            var api = new FakeCurlApi();
            int performCalls = 0;
            bool completionEnqueued = false;
            api.OnMultiPerform = multi =>
            {
                var n = Interlocked.Increment(ref performCalls);
                if (n <= 4)
                    throw new InvalidOperationException("transient");

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

            // 经历 4 次连续异常后，第 5 次成功时请求能正常完成，worker 未 faulted
            var resp = await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(resp.FailureException);
            Assert.False(worker.IsFaulted);
        }

        [Fact]
        public async Task WorkerLoop_ExactlyFiveConsecutiveExceptions_EntersFaulted()
        {
            var api = new FakeCurlApi();
            int performCalls = 0;
            // 恰好 5 次异常（不多不少），验证临界点
            api.OnMultiPerform = _ =>
            {
                if (Interlocked.Increment(ref performCalls) <= 5)
                    throw new InvalidOperationException("force-fault-on-5th");
            };

            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = 50 };
            worker.Start();

            var inflight = NewRequest(api, out var completion);
            worker.Send(inflight);

            var resp = await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(resp.FailureException);
            Assert.True(worker.IsFaulted);
        }

        // ---------------------------------------------------------------
        // consecutiveFailures 重置：成功后计数归零，随后异常重新起步
        // ---------------------------------------------------------------
        [Fact]
        public async Task WorkerLoop_SuccessResetsCounter_SubsequentTransientsRecover()
        {
            var api = new FakeCurlApi();
            int performCalls = 0;
            int completions = 0;
            api.OnMultiPerform = multi =>
            {
                var n = Interlocked.Increment(ref performCalls);
                // 第 1-4 次抛异常，第 5 次成功（completions=1），第 6-9 次再抛，第 10 次成功
                // 若计数没有重置，第 6-9 次异常累计到 8 次会触发 faulted（4+4>5）
                // 若计数正确重置，应该是 0→4（不 fault）→重置→0→4（不 fault）
                if (n <= 4 || (n >= 6 && n <= 9))
                    throw new InvalidOperationException("transient");

                var handle = api.GetFirstActiveHandle(multi);
                if (handle != IntPtr.Zero && Interlocked.Increment(ref completions) <= 2)
                    api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };

            using var worker = new CurlBackgroundWorker(api) { PollTimeoutMs = 50 };
            worker.Start();

            // 第一个请求：经历 4 次异常后完成
            var req1 = NewRequest(api, out var comp1);
            worker.Send(req1);
            var r1 = await comp1.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(r1.FailureException);
            Assert.False(worker.IsFaulted);

            // 第二个请求：计数已重置，再经历 4 次异常仍不 faulted
            var req2 = NewRequest(api, out var comp2);
            worker.Send(req2);
            var r2 = await comp2.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(r2.FailureException);
            Assert.False(worker.IsFaulted);
        }
    }
}
