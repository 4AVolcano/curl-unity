using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CurlUnity.Native;

namespace CurlUnity.Core
{
    /// <summary>
    /// CurlRequest 的生命周期状态。
    /// <para>
    /// 合法转换：
    /// <c>Created → Submitted</c>（<see cref="CurlMulti.Send"/> 成功后）
    /// → <c>Completed</c>（libcurl 返回完成）或 <c>Cancelled</c>（取消）
    /// → <c>Disposed</c>（资源释放完毕）。
    /// </para>
    /// <para>
    /// <c>Created → Cancelled</c> 和 <c>Created → Disposed</c> 也合法（未提交就取消）。
    /// 每个终态都是幂等的；重复调用 Dispose 安全无效。
    /// </para>
    /// </summary>
    internal enum CurlRequestState
    {
        Created = 0,
        Submitted = 1,
        Completed = 2,
        Cancelled = 3,
        Disposed = 4,
    }

    internal class CurlRequest : IDisposable
    {
        internal readonly ICurlApi Api;
        internal readonly IntPtr Handle;
        internal Action<CurlResponse> OnComplete;

        internal readonly MemoryStream BodyBuffer = new();
        internal MemoryStream HeaderBuffer;
        internal Action<byte[], int, int> DataCallback;
        internal bool CaptureHeaders;
        internal IntPtr HeaderSlist;
        internal GCHandle SelfHandle;

        // 流式上传: UploadStream 非 null 时 libcurl 通过 READFUNCTION 拉数据;
        // 回调里 Stream.Read 抛异常会被记到 UploadError,请求完成后上层据此决定是否外抛。
        // Stream 生命周期由调用方负责(不 Dispose)。
        internal Stream UploadStream;
        internal Exception UploadError;

        // 下载回调: 用户 DataCallback / BodyBuffer 写入时异常会被记到 DownloadError,
        // 请求完成后由上层 ExceptionDispatchInfo rethrow 原异常(保留栈)。
        // 与 UploadError 对称: 异常暂存 → 回调返回 0 让 libcurl 以 CURLE_WRITE_ERROR 结束请求
        // → OnComplete 时看到 DownloadError 非空,优先 rethrow 用户异常,不包 CurlHttpException。
        internal Exception DownloadError;

        private int _state = (int)CurlRequestState.Created;
        private bool _handleTransferred;

        public CurlRequest()
            : this(CurlNativeApi.Instance)
        {
        }

        internal CurlRequest(ICurlApi api)
        {
            Api = api ?? throw new ArgumentNullException(nameof(api));
            Handle = api.EasyInit();
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("curl_easy_init returned null");
        }

        internal CurlRequestState State => (CurlRequestState)Volatile.Read(ref _state);

        /// <summary>
        /// 原子地把状态从 <paramref name="from"/> 转到 <paramref name="to"/>。
        /// 竞争时返回 false，调用方应据此决定是否放弃当前操作。
        /// </summary>
        internal bool TryTransitionState(CurlRequestState from, CurlRequestState to)
        {
            return Interlocked.CompareExchange(
                ref _state, (int)to, (int)from) == (int)from;
        }

        /// <summary>
        /// 请求完成后调用：标记完成、释放辅助资源（GCHandle、slist、buffers），
        /// 但不释放 easy handle（所有权已转移给 CurlResponse）。
        /// </summary>
        internal void ReleaseBuffers()
        {
            // Submitted → Completed；已 Cancelled/Disposed 则不回退
            TryTransitionState(CurlRequestState.Submitted, CurlRequestState.Completed);

            _handleTransferred = true;

            if (SelfHandle.IsAllocated)
                SelfHandle.Free();

            if (HeaderSlist != IntPtr.Zero)
            {
                Api.SListFreeAll(HeaderSlist);
                HeaderSlist = IntPtr.Zero;
            }

            BodyBuffer.Dispose();
            HeaderBuffer?.Dispose();
        }

        /// <summary>
        /// 完整释放所有资源，包括 easy handle。用于取消等未完成的场景。幂等。
        /// </summary>
        public void Dispose()
        {
            // 如果已经是 Disposed，不重复释放；其它任意状态都能进入 Disposed。
            var previous = (CurlRequestState)Interlocked.Exchange(
                ref _state, (int)CurlRequestState.Disposed);
            if (previous == CurlRequestState.Disposed)
                return;

            if (SelfHandle.IsAllocated)
                SelfHandle.Free();

            if (HeaderSlist != IntPtr.Zero)
            {
                Api.SListFreeAll(HeaderSlist);
                HeaderSlist = IntPtr.Zero;
            }

            if (!_handleTransferred && Handle != IntPtr.Zero)
                Api.EasyCleanup(Handle);

            BodyBuffer.Dispose();
            HeaderBuffer?.Dispose();
        }
    }
}
