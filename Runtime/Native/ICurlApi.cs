using System;

namespace CurlUnity.Native
{
    internal interface ICurlApi
    {
        int CurlGlobalInit(long flags);
        void CurlGlobalCleanup();

        IntPtr EasyInit();
        void EasyCleanup(IntPtr handle);

        IntPtr SListAppend(IntPtr list, string value);
        void SListFreeAll(IntPtr list);

        int SetOptString(IntPtr handle, int option, string value);
        int SetOptLong(IntPtr handle, int option, long value);
        int SetOptPtr(IntPtr handle, int option, IntPtr value);
        int SetOptOffT(IntPtr handle, int option, long value);
        int SetOptWriteFunction(IntPtr handle, CurlNative.WriteCallback callback);
        int SetOptWriteData(IntPtr handle, IntPtr userdata);
        int SetOptHeaderFunction(IntPtr handle, CurlNative.WriteCallback callback);
        int SetOptHeaderData(IntPtr handle, IntPtr userdata);
        int SetOptReadFunction(IntPtr handle, CurlNative.WriteCallback callback);
        int SetOptReadData(IntPtr handle, IntPtr userdata);

        int GetInfoLong(IntPtr handle, int info, out long value);
        int GetInfoString(IntPtr handle, int info, out IntPtr value);
        int GetInfoDouble(IntPtr handle, int info, out double value);
        int GetInfoOffT(IntPtr handle, int info, out long value);

        IntPtr ShareInit();
        int ShareCleanup(IntPtr share);
        int ShareSetOptLong(IntPtr share, int option, long value);
        int ShareSetOptPtr(IntPtr share, int option, IntPtr value);

        IntPtr MultiInit();
        int MultiCleanup(IntPtr multi);
        int MultiAddHandle(IntPtr multi, IntPtr easy);
        int MultiRemoveHandle(IntPtr multi, IntPtr easy);
        int MultiPerform(IntPtr multi, out int runningHandles);
        int MultiPoll(IntPtr multi, IntPtr extraFds, uint extraNfds, int timeoutMs, out int numFds);
        int MultiWakeup(IntPtr multi);
        int MultiInfoRead(IntPtr multi, out IntPtr easyHandle, out int result);

        /// <summary><c>curl_easy_strerror</c> 等价物，用于 <c>CURLcode</c>（easy 系列返回值）。</summary>
        string GetErrorString(int code);

        /// <summary><c>curl_multi_strerror</c> 等价物，用于 <c>CURLMcode</c>（multi 系列返回值）。</summary>
        string GetMultiErrorString(int code);

        /// <summary><c>curl_share_strerror</c> 等价物，用于 <c>CURLSHcode</c>（share 系列返回值）。</summary>
        string GetShareErrorString(int code);
    }
}
