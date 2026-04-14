/*
 * curl_unity_bridge.c
 *
 * Non-variadic wrappers around libcurl's variadic functions
 * (curl_easy_setopt / curl_easy_getinfo).
 *
 * ARM64 ABI passes variadic arguments differently from fixed arguments.
 * P/Invoke treats all parameters as fixed, so calling variadic C functions
 * directly corrupts argument values on ARM64. This bridge solves the problem
 * by wrapping each variadic call into a fixed-argument function.
 *
 * Built into libcurl_unity_bridge.dylib / .so / .a alongside libcurl.
 */

#include <curl/curl.h>
#include <stdint.h>

/* ================================================================
 * Shared library export macros
 * ================================================================ */

#if defined(_WIN32)
  #define BRIDGE_API __declspec(dllexport)
#else
  #define BRIDGE_API __attribute__((visibility("default")))
#endif

/* ================================================================
 * curl_easy_setopt wrappers
 * ================================================================ */

BRIDGE_API int curl_unity_setopt_string(CURL *handle, int option, const char *value)
{
    return (int)curl_easy_setopt(handle, (CURLoption)option, value);
}

BRIDGE_API int curl_unity_setopt_long(CURL *handle, int option, int64_t value)
{
    return (int)curl_easy_setopt(handle, (CURLoption)option, (long)value);
}

BRIDGE_API int curl_unity_setopt_ptr(CURL *handle, int option, void *value)
{
    return (int)curl_easy_setopt(handle, (CURLoption)option, value);
}

BRIDGE_API int curl_unity_setopt_off_t(CURL *handle, int option, int64_t value)
{
    return (int)curl_easy_setopt(handle, (CURLoption)option, (curl_off_t)value);
}

/* Callback setters — typed wrappers for common callback patterns */

typedef size_t (*curl_unity_write_cb)(char *ptr, size_t size, size_t nmemb, void *userdata);

BRIDGE_API int curl_unity_setopt_write_function(CURL *handle, curl_unity_write_cb callback)
{
    return (int)curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, callback);
}

BRIDGE_API int curl_unity_setopt_write_data(CURL *handle, void *userdata)
{
    return (int)curl_easy_setopt(handle, CURLOPT_WRITEDATA, userdata);
}

BRIDGE_API int curl_unity_setopt_header_function(CURL *handle, curl_unity_write_cb callback)
{
    return (int)curl_easy_setopt(handle, CURLOPT_HEADERFUNCTION, callback);
}

BRIDGE_API int curl_unity_setopt_header_data(CURL *handle, void *userdata)
{
    return (int)curl_easy_setopt(handle, CURLOPT_HEADERDATA, userdata);
}

/* ================================================================
 * curl_easy_getinfo wrappers
 * ================================================================ */

BRIDGE_API int curl_unity_getinfo_long(CURL *handle, int info, int64_t *value)
{
    long tmp = 0;
    int ret = (int)curl_easy_getinfo(handle, (CURLINFO)info, &tmp);
    if (value) *value = (int64_t)tmp;
    return ret;
}

BRIDGE_API int curl_unity_getinfo_string(CURL *handle, int info, const char **value)
{
    return (int)curl_easy_getinfo(handle, (CURLINFO)info, value);
}

BRIDGE_API int curl_unity_getinfo_double(CURL *handle, int info, double *value)
{
    return (int)curl_easy_getinfo(handle, (CURLINFO)info, value);
}

BRIDGE_API int curl_unity_getinfo_off_t(CURL *handle, int info, int64_t *value)
{
    return (int)curl_easy_getinfo(handle, (CURLINFO)info, value);
}

/* ================================================================
 * curl_multi helpers
 *
 * Most curl_multi_* functions are non-variadic and can be called
 * via P/Invoke directly. Only curl_multi_setopt is variadic.
 * We also wrap curl_multi_info_read to avoid struct marshaling.
 * ================================================================ */

BRIDGE_API int curl_unity_multi_setopt_long(CURLM *multi, int option, int64_t value)
{
    return (int)curl_multi_setopt(multi, (CURLMoption)option, (long)value);
}

/*
 * Wraps curl_multi_info_read to avoid marshaling the CURLMsg struct.
 * Returns 1 if a completed message was found, 0 otherwise.
 */
BRIDGE_API int curl_unity_multi_info_read(CURLM *multi, CURL **easy_out, int *result_out)
{
    int msgs_in_queue;
    CURLMsg *msg = curl_multi_info_read(multi, &msgs_in_queue);
    if (!msg)
        return 0;
    if (msg->msg == CURLMSG_DONE)
    {
        *easy_out = msg->easy_handle;
        *result_out = (int)msg->data.result;
        return 1;
    }
    return 0;
}
