using System;

namespace CurlUnity.Native
{
    internal sealed class CurlNativeApi : ICurlApi
    {
        public static readonly ICurlApi Instance = new CurlNativeApi();

        private CurlNativeApi() { }

        public int CurlGlobalInit(long flags) => CurlNative.curl_global_init(flags);

        public void CurlGlobalCleanup() => CurlNative.curl_global_cleanup();

        public IntPtr EasyInit() => CurlNative.curl_easy_init();

        public void EasyCleanup(IntPtr handle) => CurlNative.curl_easy_cleanup(handle);

        public IntPtr SListAppend(IntPtr list, string value) => CurlNative.curl_slist_append(list, value);

        public void SListFreeAll(IntPtr list) => CurlNative.curl_slist_free_all(list);

        public int SetOptString(IntPtr handle, int option, string value) =>
            CurlNative.curl_unity_setopt_string(handle, option, value);

        public int SetOptLong(IntPtr handle, int option, long value) =>
            CurlNative.curl_unity_setopt_long(handle, option, value);

        public int SetOptPtr(IntPtr handle, int option, IntPtr value) =>
            CurlNative.curl_unity_setopt_ptr(handle, option, value);

        public int SetOptOffT(IntPtr handle, int option, long value) =>
            CurlNative.curl_unity_setopt_off_t(handle, option, value);

        public int SetOptWriteFunction(IntPtr handle, CurlNative.WriteCallback callback) =>
            CurlNative.curl_unity_setopt_write_function(handle, callback);

        public int SetOptWriteData(IntPtr handle, IntPtr userdata) =>
            CurlNative.curl_unity_setopt_write_data(handle, userdata);

        public int SetOptHeaderFunction(IntPtr handle, CurlNative.WriteCallback callback) =>
            CurlNative.curl_unity_setopt_header_function(handle, callback);

        public int SetOptHeaderData(IntPtr handle, IntPtr userdata) =>
            CurlNative.curl_unity_setopt_header_data(handle, userdata);

        public int GetInfoLong(IntPtr handle, int info, out long value) =>
            CurlNative.curl_unity_getinfo_long(handle, info, out value);

        public int GetInfoString(IntPtr handle, int info, out IntPtr value) =>
            CurlNative.curl_unity_getinfo_string(handle, info, out value);

        public int GetInfoDouble(IntPtr handle, int info, out double value) =>
            CurlNative.curl_unity_getinfo_double(handle, info, out value);

        public int GetInfoOffT(IntPtr handle, int info, out long value) =>
            CurlNative.curl_unity_getinfo_off_t(handle, info, out value);

        public IntPtr MultiInit() => CurlNative.curl_multi_init();

        public int MultiCleanup(IntPtr multi) => CurlNative.curl_multi_cleanup(multi);

        public int MultiAddHandle(IntPtr multi, IntPtr easy) => CurlNative.curl_multi_add_handle(multi, easy);

        public int MultiRemoveHandle(IntPtr multi, IntPtr easy) => CurlNative.curl_multi_remove_handle(multi, easy);

        public int MultiPerform(IntPtr multi, out int runningHandles) =>
            CurlNative.curl_multi_perform(multi, out runningHandles);

        public int MultiPoll(IntPtr multi, IntPtr extraFds, uint extraNfds, int timeoutMs, out int numFds) =>
            CurlNative.curl_multi_poll(multi, extraFds, extraNfds, timeoutMs, out numFds);

        public int MultiWakeup(IntPtr multi) => CurlNative.curl_multi_wakeup(multi);

        public int MultiInfoRead(IntPtr multi, out IntPtr easyHandle, out int result) =>
            CurlNative.curl_unity_multi_info_read(multi, out easyHandle, out result);

        public string GetErrorString(int code) => CurlNative.GetErrorString(code);

        public string GetMultiErrorString(int code) => CurlNative.GetMultiErrorString(code);
    }
}
