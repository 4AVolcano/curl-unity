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
    }
}
