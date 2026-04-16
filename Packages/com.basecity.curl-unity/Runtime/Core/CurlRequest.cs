using System;
using System.IO;
using System.Runtime.InteropServices;
using CurlUnity.Native;

namespace CurlUnity.Core
{
    internal class CurlRequest : IDisposable
    {
        internal readonly IntPtr Handle;
        internal Action<CurlResponse> OnComplete;

        internal readonly MemoryStream BodyBuffer = new();
        internal MemoryStream HeaderBuffer;
        internal Action<byte[], int, int> DataCallback;
        internal bool CaptureHeaders;
        internal IntPtr HeaderSlist;
        internal GCHandle SelfHandle;

        private bool _disposed;
        private bool _handleTransferred;

        public CurlRequest()
        {
            Handle = CurlNative.curl_easy_init();
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("curl_easy_init returned null");
        }

        /// <summary>
        /// 请求完成后调用：释放辅助资源（GCHandle、slist、buffers），
        /// 但不释放 easy handle（所有权已转移给 CurlResponse）。
        /// </summary>
        internal void ReleaseBuffers()
        {
            if (_disposed) return;
            _disposed = true;
            _handleTransferred = true;

            if (SelfHandle.IsAllocated)
                SelfHandle.Free();

            if (HeaderSlist != IntPtr.Zero)
            {
                CurlNative.curl_slist_free_all(HeaderSlist);
                HeaderSlist = IntPtr.Zero;
            }

            BodyBuffer.Dispose();
            HeaderBuffer?.Dispose();
        }

        /// <summary>
        /// 完整释放所有资源，包括 easy handle。用于取消等未完成的场景。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (SelfHandle.IsAllocated)
                SelfHandle.Free();

            if (HeaderSlist != IntPtr.Zero)
                CurlNative.curl_slist_free_all(HeaderSlist);

            if (!_handleTransferred && Handle != IntPtr.Zero)
                CurlNative.curl_easy_cleanup(Handle);

            BodyBuffer.Dispose();
            HeaderBuffer?.Dispose();
        }
    }
}
