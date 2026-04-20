using System;
using System.Runtime.InteropServices;

namespace CurlUnity.Native
{
    /// <summary>
    /// P/Invoke bindings for libcurl via curl_unity_bridge.
    ///
    /// Non-variadic functions (curl_global_init, curl_easy_init, curl_easy_perform, etc.)
    /// are called directly. Variadic functions (curl_easy_setopt, curl_easy_getinfo) go
    /// through the bridge wrappers to avoid ARM64 ABI issues.
    /// </summary>
    internal static class CurlNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string LIB = "__Internal";
#elif UNITY_STANDALONE_WIN
        private const string LIB = "libcurl_unity";
#else
        private const string LIB = "curl_unity";
#endif

        // curl_global_init flags
        public const long CURL_GLOBAL_SSL = 1 << 0;
        public const long CURL_GLOBAL_WIN32 = 1 << 1;
        public const long CURL_GLOBAL_ALL = CURL_GLOBAL_SSL | CURL_GLOBAL_WIN32;
        public const long CURL_GLOBAL_DEFAULT = CURL_GLOBAL_ALL;

        // Common CURLOPT values
        public const int CURLOPT_URL = 10002;
        public const int CURLOPT_PROXY = 10004;
        public const int CURLOPT_WRITEFUNCTION = 20011;
        public const int CURLOPT_WRITEDATA = 10001;
        public const int CURLOPT_HEADERFUNCTION = 20079;
        public const int CURLOPT_HEADERDATA = 10029;
        public const int CURLOPT_FOLLOWLOCATION = 52;
        public const int CURLOPT_TIMEOUT = 13;
        public const int CURLOPT_CAINFO = 10065;
        public const int CURLOPT_SSL_VERIFYPEER = 64;
        public const int CURLOPT_SSL_VERIFYHOST = 81;
        public const int CURLOPT_HTTPHEADER = 10023;
        public const int CURLOPT_CUSTOMREQUEST = 10036;
        public const int CURLOPT_POSTFIELDS = 10015;
        public const int CURLOPT_POSTFIELDSIZE = 60;
        public const int CURLOPT_USERAGENT = 10018;
        public const int CURLOPT_HTTP_VERSION = 84;
        public const int CURLOPT_ALTSVC = 10230;
        public const int CURLOPT_CONNECTTIMEOUT_MS = 156;
        public const int CURLOPT_TIMEOUT_MS = 155;
        public const int CURLOPT_COOKIELIST = 10135;
        public const int CURLOPT_COPYPOSTFIELDS = 10165;
        public const int CURLOPT_POSTFIELDSIZE_LARGE = 30120;
        public const int CURLOPT_POST = 47;
        public const int CURLOPT_NOBODY = 44;
        public const int CURLOPT_NOSIGNAL = 99;
        public const int CURLOPT_SSL_OPTIONS = 216;
        public const long CURLSSLOPT_NATIVE_CA = 1 << 4;  // Use OS native CA store (Windows CryptoAPI)

        // CURL_HTTP_VERSION values (用于 CURLOPT_HTTP_VERSION 设置，也是 CURLINFO_HTTP_VERSION 返回值)
        public const long CURL_HTTP_VERSION_NONE = 0;
        public const long CURL_HTTP_VERSION_1_0 = 1;
        public const long CURL_HTTP_VERSION_1_1 = 2;
        public const long CURL_HTTP_VERSION_2 = 3;
        public const long CURL_HTTP_VERSION_3 = 30;
        public const long CURL_HTTP_VERSION_3ONLY = 31;

        // CURLINFO 类型前缀: STRING=0x100000, LONG=0x200000, DOUBLE=0x300000, OFF_T=0x600000
        public const int CURLINFO_EFFECTIVE_URL = 0x100001;                // STRING+1
        public const int CURLINFO_RESPONSE_CODE = 0x200002;                // LONG+2
        public const int CURLINFO_TOTAL_TIME = 0x300003;                   // DOUBLE+3
        public const int CURLINFO_CONTENT_TYPE = 0x100012;                 // STRING+18
        public const int CURLINFO_REDIRECT_COUNT = 0x200014;               // LONG+20
        public const int CURLINFO_HTTP_VERSION = 0x20002E;                 // LONG+46
        public const int CURLINFO_NUM_CONNECTS = 0x20001A;                 // LONG+26
        public const int CURLINFO_CONTENT_LENGTH_DOWNLOAD_T = 0x60000F;    // OFF_T+15

        // Timing (OFF_T = 0x600000, 微秒精度)
        public const int CURLINFO_TOTAL_TIME_T = 0x600032;                 // OFF_T+50
        public const int CURLINFO_NAMELOOKUP_TIME_T = 0x600033;            // OFF_T+51
        public const int CURLINFO_CONNECT_TIME_T = 0x600034;               // OFF_T+52
        public const int CURLINFO_APPCONNECT_TIME_T = 0x600038;            // OFF_T+56
        public const int CURLINFO_STARTTRANSFER_TIME_T = 0x600036;         // OFF_T+54
        public const int CURLINFO_REDIRECT_TIME_T = 0x600037;              // OFF_T+55
        public const int CURLINFO_SIZE_DOWNLOAD_T = 0x600008;              // OFF_T+8
        public const int CURLINFO_SIZE_UPLOAD_T = 0x600007;                // OFF_T+7
        public const int CURLINFO_SPEED_DOWNLOAD_T = 0x600009;             // OFF_T+9
        public const int CURLINFO_CONN_ID = 0x600040;                      // OFF_T+64
        public const int CURLINFO_XFER_ID = 0x60003F;                      // OFF_T+63

        // CURLcode
        public const int CURLE_OK = 0;

        // Write callback: size_t (*)(char *ptr, size_t size, size_t nmemb, void *userdata)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate UIntPtr WriteCallback(IntPtr ptr, UIntPtr size, UIntPtr nmemb, IntPtr userdata);

        /* ==============================================================
         * Direct libcurl calls (non-variadic, safe on all platforms)
         * ============================================================== */

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_global_init(long flags);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void curl_global_cleanup();

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr curl_easy_init();

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void curl_easy_cleanup(IntPtr handle);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_easy_perform(IntPtr handle);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr curl_easy_strerror(int code);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr curl_multi_strerror(int code);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr curl_version();

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr curl_slist_append(IntPtr list,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string val);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void curl_slist_free_all(IntPtr list);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void curl_free(IntPtr ptr);

        /* ==============================================================
         * Bridge wrappers (for variadic curl_easy_setopt / curl_easy_getinfo)
         * ============================================================== */

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_string(IntPtr handle, int option,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_long(IntPtr handle, int option, long value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_ptr(IntPtr handle, int option, IntPtr value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_off_t(IntPtr handle, int option, long value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_write_function(IntPtr handle, WriteCallback callback);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_write_data(IntPtr handle, IntPtr userdata);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_header_function(IntPtr handle, WriteCallback callback);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_setopt_header_data(IntPtr handle, IntPtr userdata);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_getinfo_long(IntPtr handle, int info, out long value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_getinfo_string(IntPtr handle, int info, out IntPtr value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_getinfo_double(IntPtr handle, int info, out double value);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_getinfo_off_t(IntPtr handle, int info, out long value);

        /* ==============================================================
         * curl_multi — non-variadic, direct P/Invoke
         * ============================================================== */

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr curl_multi_init();

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_multi_cleanup(IntPtr multi);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_multi_add_handle(IntPtr multi, IntPtr easy);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_multi_remove_handle(IntPtr multi, IntPtr easy);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_multi_perform(IntPtr multi, out int runningHandles);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_multi_poll(IntPtr multi, IntPtr extraFds, uint extraNfds,
            int timeoutMs, out int numFds);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_multi_wakeup(IntPtr multi);

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern void curl_easy_reset(IntPtr handle);

        /* ==============================================================
         * curl_multi — bridge wrappers (variadic or struct marshaling)
         * ============================================================== */

        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_multi_setopt_long(IntPtr multi, int option, long value);

        /// <summary>
        /// Wraps curl_multi_info_read. Returns 1 if a completed handle was found.
        /// </summary>
        [DllImport(LIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int curl_unity_multi_info_read(IntPtr multi, out IntPtr easyHandle, out int result);

        // CURLMOPT constants
        public const int CURLMOPT_MAX_TOTAL_CONNECTIONS = 13;
        public const int CURLMOPT_MAX_HOST_CONNECTIONS = 7;
        public const int CURLMOPT_MAXCONNECTS = 6;

        // CURLOPT_PRIVATE — associate user pointer with easy handle
        public const int CURLOPT_PRIVATE = 10103;

        // CURLINFO_PRIVATE — retrieve user pointer
        public const int CURLINFO_PRIVATE = 0x100015;

        /* ==============================================================
         * Helpers
         * ============================================================== */

        public static int SetUrl(IntPtr handle, string url)
            => curl_unity_setopt_string(handle, CURLOPT_URL, url);

        public static string GetVersionString()
            => Marshal.PtrToStringAnsi(curl_version()) ?? "";

        /// <summary>
        /// 把 <c>CURLcode</c>（<c>curl_easy_*</c> 系列函数的返回值）转成文本。
        /// 用于 <c>curl_multi_*</c> 返回的 <c>CURLMcode</c> 会得到错误的字符串，
        /// 那种情况请改用 <see cref="GetMultiErrorString"/>。
        /// </summary>
        public static string GetErrorString(int code)
            => Marshal.PtrToStringAnsi(curl_easy_strerror(code)) ?? "";

        /// <summary>
        /// 把 <c>CURLMcode</c>（<c>curl_multi_*</c> 系列函数的返回值）转成文本。
        /// </summary>
        public static string GetMultiErrorString(int code)
            => Marshal.PtrToStringAnsi(curl_multi_strerror(code)) ?? "";
    }
}
