using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CurlUnity.Core;
using CurlUnity.Http;
using CurlUnity.Native;
using CurlUnity.UnitTests.TestSupport;
using Xunit;

namespace CurlUnity.UnitTests.Tests
{
    /// <summary>
    /// 单元测试 CurlMulti 里用 FakeCurlApi 能隔离测到的关键路径:
    ///   * Cancel() 在 remove_handle 失败时的 leak-over-crash 防御
    ///   * ProcessCompletion 里 remove_handle 失败 / CURLINFO_PRIVATE 解析失败
    ///   * native 回调 (OnWriteData / OnReadData) 在 GCHandle resolve 失败 + 用户
    ///     callback 抛异常时的行为
    /// 这些路径真实触发要么需要 curl 内部错误(实际碰不到), 要么 race condition,
    /// 集成测试跑不出。FakeCurlApi 注入让我们精确触发每个分支。
    /// </summary>
    public class CurlMultiTests
    {
        // ================================================================
        // Cancel() 的 leak-over-crash 分支
        // ================================================================

        [Fact]
        public void Cancel_WhenSubmitted_NormalPath_DisposesHandleAfterRemove()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            CurlResponse completed = null;
            var req = new CurlRequest(api) { OnComplete = r => completed = r };

            multi.Send(req);
            Assert.Equal(CurlRequestState.Submitted, req.State);

            multi.Cancel(req);

            Assert.Equal(CurlRequestState.Disposed, req.State);  // Dispose 后终态
            Assert.True(api.GetEasyHandleState(req.Handle).IsCleanedUp);
            // cancel-via-Cancel 不走 OnComplete (上层 CurlHttpClient.SendAsync 已经
            // 通过 ct.Register 的 TrySetCanceled 负责 Task 状态)
            Assert.Null(completed);
        }

        [Fact]
        public void Cancel_WhenSubmittedAndRemoveHandleFails_LeaksEasyHandleToAvoidUAF()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            CurlResponse completed = null;
            var req = new CurlRequest(api) { OnComplete = r => completed = r };

            multi.Send(req);
            api.MultiRemoveHandleResult = CurlNative.CURLE_OK + 1;  // 任意非 OK 值

            multi.Cancel(req);

            // 关键 invariant: remove_handle 失败 → libcurl multi 仍持有此 easy handle,
            // 这时如果 EasyCleanup 就是 use-after-free。我们故意**不** Dispose,
            // 让 handle 泄漏,等 multi 自己 cleanup 时兜底。
            Assert.False(api.GetEasyHandleState(req.Handle).IsCleanedUp,
                "remove_handle 失败时 easy handle 必须保持存活, 否则 UAF");
            Assert.Equal(CurlRequestState.Cancelled, req.State);
            Assert.Null(completed);

            // 断言完成后恢复: 本测试 using var multi 退出时 Dispose 会遍历 _activeRequests
            // 再次 MultiRemoveHandle, 如果这里仍是失败值, multi 会继续跳过 request.Dispose,
            // CurlRequest.SelfHandle 持有的 GCHandle 就永远泄漏到测试进程里。
            // 生产场景里 libcurl 自然会返回 OK, 这里手动 reset 模拟。
            api.MultiRemoveHandleResult = CurlNative.CURLE_OK;
        }

        [Fact]
        public void Cancel_WhenNotSubmittedYet_RunsFailCompleteWithOperationCanceled()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            CurlResponse completed = null;
            using var req = new CurlRequest(api) { OnComplete = r => completed = r };

            // 没调 Send, 直接 Cancel: Created → Cancelled, 走 FailComplete
            multi.Cancel(req);

            Assert.Equal(CurlRequestState.Disposed, req.State);
            Assert.NotNull(completed);
            Assert.IsType<OperationCanceledException>(completed.FailureException);
        }

        // ================================================================
        // ProcessCompletion 的错误路径
        // ================================================================

        [Fact]
        public void ProcessCompletion_RemoveHandleFailure_CompletesWithSetupFailedException()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            CurlResponse completed = null;
            using var req = new CurlRequest(api) { OnComplete = r => completed = r };
            multi.Send(req);

            // 模拟 libcurl 完成请求, 但 multi 端 remove 失败
            api.EnqueueCompletion(req.Handle, CurlNative.CURLE_OK);
            api.MultiRemoveHandleResult = CurlNative.CURLE_OK + 2;

            multi.Tick();

            Assert.NotNull(completed);
            var ex = Assert.IsType<CurlHttpException>(completed.FailureException);
            Assert.Equal(HttpErrorKind.SetupFailed, ex.ErrorKind);
            // 所有权没转移: CurlResponse.EasyHandle 保持 Zero, 由 multi.Dispose 兜底
            Assert.Equal(IntPtr.Zero, completed.EasyHandle);
        }

        [Fact]
        public void ProcessCompletion_PrivateInfoResolveFailure_SkipsCompletionCallback()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            CurlResponse completed = null;
            using var req = new CurlRequest(api) { OnComplete = r => completed = r };
            multi.Send(req);

            api.EnqueueCompletion(req.Handle, CurlNative.CURLE_OK);
            // 注入 PRIVATE 查询失败 → ProcessCompletion 拿不到 request 指针,
            // 视作 stray handle, 只 log + 尝试 remove, 不 invoke OnComplete
            api.GetInfoStringHook = (h, info) =>
                info == CurlNative.CURLINFO_PRIVATE
                    ? (CurlNative.CURLE_OK + 3, IntPtr.Zero)
                    : null;

            multi.Tick();

            Assert.Null(completed);  // 没法通知, 也不该瞎通知别的 request
        }

        // ================================================================
        // OnWriteData native callback 错误路径
        // ================================================================

        [Fact]
        public void OnWriteData_WhenUserCallbackThrows_RecordsDownloadErrorAndAborts()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var req = new CurlRequest(api)
            {
                DataCallback = (buf, off, count) => throw new IOException("user code boom"),
                OnComplete = _ => { },
            };
            multi.Send(req);

            api.InvokeWriteCallback(req.Handle, new byte[] { 1, 2, 3, 4, 5 });

            Assert.NotNull(req.DownloadError);
            var io = Assert.IsType<IOException>(req.DownloadError);
            Assert.Equal("user code boom", io.Message);
        }

        [Fact]
        public void OnWriteData_StreamedChunks_DoNotAllocatePerChunkAfterPoolWarmup()
        {
            const int chunkSize = 16 * 1024;
            const int iterations = 64;
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            int callbackCount = 0;
            using var req = new CurlRequest(api)
            {
                DataCallback = (_, _, _) => callbackCount++,
                OnComplete = _ => { },
            };
            multi.Send(req);

            var payload = new byte[chunkSize];
            api.InvokeWriteCallback(req.Handle, payload); // 预热 JIT / ArrayPool bucket

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
                api.InvokeWriteCallback(req.Handle, payload);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(iterations + 1, callbackCount);
            Assert.True(allocated < chunkSize,
                $"稳态流式回调不应按 chunk 分配；{iterations} 次共分配 {allocated} bytes");
        }

        [Fact]
        public void OnWriteData_WhenGCHandleTargetIsWrongType_ReturnsZeroWithoutCrashing()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var req = new CurlRequest(api) { OnComplete = _ => { } };
            multi.Send(req);

            // 给 userdata 一个合法的 GCHandle, 但它指向 "不是 CurlRequest" 的对象,
            // (CurlRequest)target 会抛 InvalidCastException。OnWriteData 的 catch
            // 必须把它吞掉, 返回 UIntPtr.Zero (让 curl 以 WRITE_ERROR 收尾)。
            // 注: 不能用完全非法的 IntPtr, 那会是 AccessViolation (非 managed exception),
            // 进程直接崩。真实场景里 userdata 要么来自我们自己的 GCHandle.Alloc, 要么是 0 —
            // 没有"随便一个 IntPtr"的路径, 所以这个分支覆盖 cast 失败就够了。
            var strayHandle = GCHandle.Alloc("not-a-request");
            try
            {
                var returned = api.InvokeWriteCallbackWithUserdata(
                    req.Handle,
                    new byte[] { 1, 2, 3 },
                    GCHandle.ToIntPtr(strayHandle));

                Assert.Equal(UIntPtr.Zero, returned);
                Assert.Null(req.DownloadError);  // 不是用户 DataCallback 抛的
            }
            finally
            {
                strayHandle.Free();
            }
        }

        // ================================================================
        // OnReadData 上传 callback
        // ================================================================

        [Fact]
        public void OnReadData_WhenStreamReadThrows_RecordsUploadErrorAndAborts()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var throwingStream = new ThrowOnReadStream(new IOException("upload source failed"));
            using var req = new CurlRequest(api)
            {
                UploadStream = throwingStream,
                OnComplete = _ => { },
            };
            multi.Send(req);

            var written = api.InvokeReadCallback(req.Handle, capacity: 1024);

            Assert.Equal(CurlNative.CURL_READFUNC_ABORT, written);
            Assert.NotNull(req.UploadError);
            var io = Assert.IsType<IOException>(req.UploadError);
            Assert.Equal("upload source failed", io.Message);
        }

        [Fact]
        public void OnReadData_WhenStreamAtEOF_ReturnsZero()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var emptyStream = new MemoryStream(Array.Empty<byte>());
            using var req = new CurlRequest(api)
            {
                UploadStream = emptyStream,
                OnComplete = _ => { },
            };
            multi.Send(req);

            var written = api.InvokeReadCallback(req.Handle, capacity: 1024);

            Assert.Equal(0, written);
            Assert.Null(req.UploadError);
        }

        [Fact]
        public void OnReadData_WhenRequestAlreadyCancelled_AbortsWithoutTouchingStream()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var trackingStream = new CountingStream(new byte[100]);
            using var req = new CurlRequest(api)
            {
                UploadStream = trackingStream,
                OnComplete = _ => { },
            };
            multi.Send(req);

            // 模拟取消竞态: 请求已转 Cancelled, 这时 read callback 仍被 libcurl 调一次,
            // 必须 ABORT 避免读已被调用方释放的 stream。
            req.TryTransitionState(CurlRequestState.Submitted, CurlRequestState.Cancelled);

            var written = api.InvokeReadCallback(req.Handle, capacity: 1024);

            Assert.Equal(CurlNative.CURL_READFUNC_ABORT, written);
            Assert.Equal(0, trackingStream.TotalReadCount);  // stream 没被碰
        }

        // ================================================================
        // OnHeaderData header callback — best-effort 契约
        // ================================================================

        [Fact]
        public void OnHeaderData_HappyPath_AppendsToHeaderBufferAndConsumes()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var req = new CurlRequest(api) { CaptureHeaders = true, OnComplete = _ => { } };
            multi.Send(req);

            var line = System.Text.Encoding.ASCII.GetBytes("X-Test: 1\r\n");
            var returned = api.InvokeHeaderCallback(req.Handle, line);

            Assert.Equal((UIntPtr)line.Length, returned);
            Assert.Equal(line, req.HeaderBuffer.ToArray());
        }

        [Fact]
        public void OnHeaderData_WhenGCHandleTargetIsWrongType_ConsumesInsteadOfAbortingTransfer()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var req = new CurlRequest(api) { CaptureHeaders = true, OnComplete = _ => { } };
            multi.Send(req);

            // header capture 是 best-effort: resolve 失败只该丢 header, 不该返回 0
            // 让 curl 以 CURLE_WRITE_ERROR 把整个传输打断(与 OnWriteData 刻意不同)。
            var strayHandle = GCHandle.Alloc("not-a-request");
            try
            {
                var payload = new byte[] { 1, 2, 3 };
                var returned = api.InvokeHeaderCallbackWithUserdata(
                    req.Handle, payload, GCHandle.ToIntPtr(strayHandle));

                Assert.Equal((UIntPtr)payload.Length, returned);
            }
            finally
            {
                strayHandle.Free();
            }
        }

        [Fact]
        public void OnHeaderData_WhenBufferWriteFails_DisablesCaptureAndContinuesTransfer()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var req = new CurlRequest(api) { CaptureHeaders = true, OnComplete = _ => { } };
            multi.Send(req);

            // 模拟 HeaderBuffer 写入失败(ObjectDisposedException): 传输必须继续,
            // 且残缺的 buffer 被整体丢弃(截断的 headers 比缺失更危险)。
            req.HeaderBuffer.Dispose();

            var line = System.Text.Encoding.ASCII.GetBytes("X-Test: 1\r\n");
            var returned = api.InvokeHeaderCallback(req.Handle, line);

            Assert.Equal((UIntPtr)line.Length, returned);
            Assert.Null(req.HeaderBuffer);  // capture 已禁用 → response.Headers 将为 null

            // 后续 header 块同样只消费不中断
            var next = api.InvokeHeaderCallback(req.Handle, line);
            Assert.Equal((UIntPtr)line.Length, next);
        }

        [Fact]
        public void OnHeaderData_Complete200_FiresHeadersReceivedBeforeBody()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            var events = new System.Collections.Generic.List<string>();
            using var req = new CurlRequest(api)
            {
                HeadersReceivedCallback = (statusCode, rawHeaders) =>
                {
                    Assert.Equal(200, statusCode);
                    Assert.Null(rawHeaders); // observation must not force response-header capture
                    events.Add("headers");
                },
                DataCallback = (_, _, _) => events.Add("body"),
                OnComplete = _ => { },
            };
            multi.Send(req);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/1.1 200 OK\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("Content-Type: text/plain\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Equal(new[] { "headers" }, events);

            api.InvokeWriteCallback(req.Handle, Ascii("body"));
            Assert.Equal(new[] { "headers", "body" }, events);
        }

        [Fact]
        public void Send_WhenHeadersAreObserved_SuppressesProxyConnectHeaders()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            using var req = new CurlRequest(api)
            {
                HeadersReceivedCallback = (_, _) => { },
                OnComplete = _ => { },
            };

            multi.Send(req);

            var state = api.GetEasyHandleState(req.Handle);
            Assert.Equal(1, state.LongOptions[CurlNative.CURLOPT_SUPPRESS_CONNECT_HEADERS]);
        }

        [Fact]
        public void OnHeaderData_Informational103_IsIgnoredUntilFinalResponseCompletes()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            long observedStatus = 0;
            byte[] observedHeaders = null;
            using var req = new CurlRequest(api)
            {
                CaptureHeaders = true,
                HeadersReceivedCallback = (statusCode, rawHeaders) =>
                {
                    observedStatus = statusCode;
                    observedHeaders = rawHeaders;
                },
                OnComplete = _ => { },
            };
            multi.Send(req);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/1.1 103 Early Hints\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("Link: </app.css>; rel=preload\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Equal(0, observedStatus);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/1.1 200 OK\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("Content-Type: text/event-stream\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Equal(200, observedStatus);
            Assert.Equal(
                Ascii("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n"),
                observedHeaders);
        }

        [Fact]
        public void OnHeaderData_Followed302_IsIgnoredUntilFinalResponseCompletes()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            var observedStatuses = new System.Collections.Generic.List<long>();
            using var req = new CurlRequest(api)
            {
                CaptureHeaders = true,
                HeadersReceivedCallback = (statusCode, _) => observedStatuses.Add(statusCode),
                OnComplete = _ => { },
            };
            multi.Send(req);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/1.1 302 Found\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("Location: /final\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Empty(observedStatuses);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/2 200\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Equal(new long[] { 200 }, observedStatuses);
        }

        [Fact]
        public async Task OnHeaderData_RedirectFollowingDisabled_FiresCompleted302Block()
        {
            var api = new FakeCurlApi();
            var callbackFired = false;
            var firedBeforeCompletionWasQueued = false;
            var droveResponse = false;
            api.OnMultiPerform = multiHandle =>
            {
                var handle = api.GetFirstActiveHandle(multiHandle);
                if (handle == IntPtr.Zero || droveResponse)
                    return;

                droveResponse = true;
                api.GetEasyHandleState(handle).ResponseCode = 302;
                api.InvokeHeaderCallback(handle, Ascii("HTTP/1.1 302 Found\r\n"));
                api.InvokeHeaderCallback(handle, Ascii("Location: /manual\r\n"));
                api.InvokeHeaderCallback(handle, Ascii("\r\n"));
                firedBeforeCompletionWasQueued = callbackFired;
                api.EnqueueCompletion(handle, CurlNative.CURLE_OK);
            };

            using var client = new CurlHttpClient(api);
            using var response = await client.SendAsync(new HttpRequest
            {
                Url = "http://example.invalid/",
                FollowRedirects = false,
                EnableResponseHeaders = true,
                OnHeadersReceived = earlyResponse =>
                {
                    Assert.Equal(302, earlyResponse.StatusCode);
                    callbackFired = true;
                },
            }).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(firedBeforeCompletionWasQueued,
                "disabled redirects must expose the completed 302 block before transfer completion");
        }

        [Fact]
        public void OnHeaderData_Trailers_DoNotFireHeadersReceivedAgain()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            var callbackCount = 0;
            using var req = new CurlRequest(api)
            {
                CaptureHeaders = true,
                HeadersReceivedCallback = (_, _) => callbackCount++,
                OnComplete = _ => { },
            };
            multi.Send(req);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/1.1 200 OK\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("Transfer-Encoding: chunked\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));
            Assert.Equal(1, callbackCount);

            api.InvokeHeaderCallback(req.Handle, Ascii("Digest: sha-256=:abc=\r\n"));
            api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Equal(1, callbackCount);
        }

        [Fact]
        public void OnHeaderData_WhenHeadersReceivedCallbackThrows_RecordsOriginalErrorAndAborts()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);
            var expected = new IOException("headers callback failed");
            using var req = new CurlRequest(api)
            {
                CaptureHeaders = true,
                HeadersReceivedCallback = (_, _) => throw expected,
                OnComplete = _ => { },
            };
            multi.Send(req);

            api.InvokeHeaderCallback(req.Handle, Ascii("HTTP/1.1 200 OK\r\n"));
            var returned = api.InvokeHeaderCallback(req.Handle, Ascii("\r\n"));

            Assert.Equal(UIntPtr.Zero, returned);
            Assert.Same(expected, req.DownloadError);
        }

        // ================================================================
        // 连接数上限 (CURLMOPT_MAX_TOTAL_CONNECTIONS / MAX_HOST_CONNECTIONS)
        // ================================================================

        [Fact]
        public void Constructor_AppliesDefaultConnectionLimits()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api);

            var state = api.GetMultiHandleState(api.LastMultiHandle);
            Assert.Equal(CurlMulti.DefaultMaxTotalConnections,
                state.LongOptions[CurlNative.CURLMOPT_MAX_TOTAL_CONNECTIONS]);
            Assert.Equal(CurlMulti.DefaultMaxHostConnections,
                state.LongOptions[CurlNative.CURLMOPT_MAX_HOST_CONNECTIONS]);
        }

        [Fact]
        public void Constructor_ZeroConnectionLimits_SkipsSetopt()
        {
            var api = new FakeCurlApi();
            using var multi = new CurlMulti(api, maxTotalConnections: 0, maxHostConnections: 0);

            var state = api.GetMultiHandleState(api.LastMultiHandle);
            Assert.False(state.LongOptions.ContainsKey(CurlNative.CURLMOPT_MAX_TOTAL_CONNECTIONS));
            Assert.False(state.LongOptions.ContainsKey(CurlNative.CURLMOPT_MAX_HOST_CONNECTIONS));
        }

        [Fact]
        public void Constructor_NegativeConnectionLimits_Throws()
        {
            var api = new FakeCurlApi();
            Assert.Throws<ArgumentOutOfRangeException>(() => new CurlMulti(api, maxTotalConnections: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CurlMulti(api, maxHostConnections: -1));
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static byte[] Ascii(string value) => System.Text.Encoding.ASCII.GetBytes(value);

        private sealed class ThrowOnReadStream : Stream
        {
            private readonly Exception _toThrow;
            public ThrowOnReadStream(Exception toThrow) { _toThrow = toThrow; }

            public override int Read(byte[] buffer, int offset, int count) => throw _toThrow;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class CountingStream : Stream
        {
            private readonly MemoryStream _inner;
            public int TotalReadCount { get; private set; }

            public CountingStream(byte[] data) { _inner = new MemoryStream(data); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                TotalReadCount++;
                return _inner.Read(buffer, offset, count);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
    }
}
