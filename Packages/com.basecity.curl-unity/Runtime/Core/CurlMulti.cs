using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
#if UNITY_5_3_OR_NEWER
using AOT;
#endif
using CurlUnity.Native;

namespace CurlUnity.Core
{
    /// <summary>
    /// curl_multi handle 封装。管理 easy handle 的生命周期、I/O 驱动和完成通知。
    ///
    /// 本类不包含线程逻辑，通过 Tick() 驱动。调用方选择驱动方式:
    ///   - 主线程: 在 MonoBehaviour.Update() 中调用 Tick()
    ///   - 后台线程: 使用 CurlBackgroundWorker，或自行在线程中调 Tick() + Poll()
    ///
    /// 线程安全: 本类自身不是线程安全的。Wakeup() 是唯一例外，可从任意线程调用。
    /// </summary>
    internal class CurlMulti : IDisposable
    {
        private IntPtr _multi;
        private bool _disposed;
        private readonly HashSet<CurlRequest> _activeRequests = new();

        public CurlMulti()
        {
            _multi = CurlNative.curl_multi_init();
            if (_multi == IntPtr.Zero)
                throw new InvalidOperationException("curl_multi_init returned null");
        }

        /// <summary>
        /// 提交请求。自动配置 write/header callback 和 PRIVATE 关联。
        /// 提交后 CurlRequest 的生命周期由 CurlMulti 管理，完成后自动 Dispose。
        /// </summary>
        public void Send(CurlRequest request)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CurlMulti));

            request.SelfHandle = GCHandle.Alloc(request);
            var ptr = GCHandle.ToIntPtr(request.SelfHandle);

            // write callback: 流式模式转发到 DataCallback，否则写入 BodyBuffer
            CurlNative.curl_unity_setopt_write_function(request.Handle, OnWriteData);
            CurlNative.curl_unity_setopt_write_data(request.Handle, ptr);

            // header callback: 仅在 CaptureHeaders 时设置
            if (request.CaptureHeaders)
            {
                request.HeaderBuffer = new MemoryStream(2048);
                CurlNative.curl_unity_setopt_header_function(request.Handle, OnHeaderData);
                CurlNative.curl_unity_setopt_header_data(request.Handle, ptr);
            }

            // PRIVATE 关联
            CurlNative.curl_unity_setopt_ptr(request.Handle, CurlNative.CURLOPT_PRIVATE, ptr);

            // CA 证书
            CurlCerts.ApplyTo(request.Handle);

            _activeRequests.Add(request);
            CurlNative.curl_multi_add_handle(_multi, request.Handle);
        }

        public void Tick()
        {
            if (_disposed) return;

            CurlNative.curl_multi_perform(_multi, out _);

            while (CurlNative.curl_unity_multi_info_read(_multi, out var easyHandle, out var curlCode) == 1)
            {
                ProcessCompletion(easyHandle, curlCode);
            }
        }

        public void Poll(int timeoutMs)
        {
            if (_disposed) return;
            CurlNative.curl_multi_poll(_multi, IntPtr.Zero, 0, timeoutMs, out _);
        }

        /// <summary>线程安全，可从任意线程调用。</summary>
        public void Wakeup()
        {
            if (_disposed) return;
            CurlNative.curl_multi_wakeup(_multi);
        }

        /// <summary>
        /// 从 multi 中移除并释放指定 easy handle。用于取消请求。
        /// 必须在驱动线程上调用（与 Tick 同一线程）。
        /// </summary>
        internal void Cancel(CurlRequest request)
        {
            if (_disposed) return;
            _activeRequests.Remove(request);
            CurlNative.curl_multi_remove_handle(_multi, request.Handle);
            request.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 清理所有仍在执行的请求（释放 GCHandle 和 easy handle）
            foreach (var request in _activeRequests)
            {
                CurlNative.curl_multi_remove_handle(_multi, request.Handle);
                request.Dispose();
            }
            _activeRequests.Clear();

            if (_multi != IntPtr.Zero)
            {
                CurlNative.curl_multi_cleanup(_multi);
                _multi = IntPtr.Zero;
            }
        }

        private void ProcessCompletion(IntPtr easyHandle, int curlCode)
        {
            CurlNative.curl_unity_getinfo_string(easyHandle, CurlNative.CURLINFO_PRIVATE, out var ptr);
            var request = (CurlRequest)GCHandle.FromIntPtr(ptr).Target;

            _activeRequests.Remove(request);

            CurlNative.curl_unity_getinfo_long(easyHandle, CurlNative.CURLINFO_RESPONSE_CODE, out var statusCode);

            var response = new CurlResponse
            {
                CurlCode = curlCode,
                StatusCode = statusCode,
                Body = request.DataCallback != null ? null : request.BodyBuffer.ToArray(),
                RawHeaders = request.HeaderBuffer?.ToArray(),
                EasyHandle = request.Handle  // 所有权转移
            };

            CurlNative.curl_multi_remove_handle(_multi, easyHandle);

            try { request.OnComplete?.Invoke(response); }
            catch (Exception) { /* TODO: 接入日志系统 */ }

            request.ReleaseBuffers();  // 释放辅助资源，不释放 easy handle
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.WriteCallback))]
#endif
        private static UIntPtr OnWriteData(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata)
        {
            var length = size.ToUInt64() * nmemb.ToUInt64();
            if (length == 0) return UIntPtr.Zero;
            if (length > int.MaxValue) return UIntPtr.Zero; // 超出托管内存单次处理能力，通知 curl 中止
            var totalBytes = (int)length;

            try
            {
                var request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target;
                var buffer = new byte[totalBytes];
                Marshal.Copy(ptr, buffer, 0, totalBytes);

                if (request.DataCallback != null)
                    request.DataCallback(buffer, 0, totalBytes);
                else
                    request.BodyBuffer.Write(buffer, 0, totalBytes);
            }
            catch
            {
                return UIntPtr.Zero;
            }

            return (UIntPtr)totalBytes;
        }

#if UNITY_5_3_OR_NEWER
        [MonoPInvokeCallback(typeof(CurlNative.WriteCallback))]
#endif
        private static UIntPtr OnHeaderData(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata)
        {
            var length = size.ToUInt64() * nmemb.ToUInt64();
            if (length == 0) return UIntPtr.Zero;
            if (length > int.MaxValue) return UIntPtr.Zero;
            var totalBytes = (int)length;

            try
            {
                var request = (CurlRequest)GCHandle.FromIntPtr(userdata).Target;
                var buffer = new byte[totalBytes];
                Marshal.Copy(ptr, buffer, 0, totalBytes);
                request.HeaderBuffer?.Write(buffer, 0, totalBytes);
            }
            catch
            {
                return UIntPtr.Zero;
            }

            return (UIntPtr)totalBytes;
        }
    }
}
