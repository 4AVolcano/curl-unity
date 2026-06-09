using System;

namespace CurlUnity.Core
{
    internal class CurlResponse
    {
        internal int CurlCode;
        internal long StatusCode;
        internal byte[] Body;
        internal byte[] RawHeaders;

        /// <summary>easy handle 所有权转移给 Response，由 HttpResponse.Dispose 释放。</summary>
        internal IntPtr EasyHandle;

        /// <summary>
        /// 请求在到达 libcurl 之前就失败时（例如 setopt/add_handle 调用返回非 0，或
        /// 请求在提交前已被取消），由管线填入。上层拿到后应直接以异常结束 Task，
        /// 不再尝试把它构造成 HttpResponse。EasyHandle 在这种情况下不转移所有权。
        /// </summary>
        internal Exception FailureException;
    }
}
