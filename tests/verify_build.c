/*
 * verify_build.c — Smoke test for libcurl_unity
 *
 * Verifies that the built library contains all expected components:
 *   - curl core (easy/multi handles)
 *   - Bridge functions (setopt/getinfo wrappers)
 *   - OpenSSL, nghttp2, nghttp3 (HTTP/2 + HTTP/3 support)
 *
 * Build & run (macOS):
 *   cc -o verify tests/verify_build.c -Ibuild/macos-arm64/install/include -Loutput/macOS -lcurl_unity
 *   DYLD_LIBRARY_PATH=output/macOS ./verify
 *
 * Build & run (Windows, from VS command prompt):
 *   cl /I"build\windows-x64\install\include" tests\verify_build.c /Fe:verify.exe /link output\Windows\x86_64\libcurl_unity.lib
 *   verify.exe
 */

#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <curl/curl.h>

/* Bridge function declarations */
extern int curl_unity_setopt_long(CURL *handle, int option, int64_t value);
extern int curl_unity_setopt_string(CURL *handle, int option, const char *value);
extern int curl_unity_getinfo_long(CURL *handle, int info, int64_t *value);
extern int curl_unity_getinfo_string(CURL *handle, int info, const char **value);
extern int curl_unity_multi_info_read(CURLM *multi, CURL **easy_out, int *result_out);
extern int curl_unity_multi_setopt_long(CURLM *multi, int option, int64_t value);

static int failures = 0;

#define CHECK(cond, fmt, ...) do { \
    if (!(cond)) { \
        printf("  FAIL: " fmt "\n", ##__VA_ARGS__); \
        failures++; \
    } \
} while(0)

static void test_version(void) {
    printf("[version]\n");
    const char *ver = curl_version();
    CHECK(ver != NULL, "curl_version() returned NULL");
    if (!ver) return;
    printf("  %s\n", ver);

    CHECK(strstr(ver, "OpenSSL") != NULL || strstr(ver, "SecureTransport") != NULL,
          "SSL library not found in version string");
    CHECK(strstr(ver, "nghttp2") != NULL, "nghttp2 (HTTP/2) not found");
    CHECK(strstr(ver, "nghttp3") != NULL, "nghttp3 (HTTP/3) not found");
    CHECK(strstr(ver, "ngtcp2") != NULL, "ngtcp2 (QUIC) not found");
}

static void test_easy_handle(void) {
    printf("[easy handle]\n");
    CURL *easy = curl_easy_init();
    CHECK(easy != NULL, "curl_easy_init returned NULL");
    if (!easy) return;

    /* bridge: setopt_long — CURLOPT_FOLLOWLOCATION = 52 */
    int rc = curl_unity_setopt_long(easy, 52, 1);
    CHECK(rc == CURLE_OK, "setopt_long(FOLLOWLOCATION) rc=%d", rc);

    /* bridge: setopt_string — CURLOPT_URL = 10002 */
    rc = curl_unity_setopt_string(easy, 10002, "https://example.com");
    CHECK(rc == CURLE_OK, "setopt_string(URL) rc=%d", rc);

    /* bridge: getinfo_long — CURLINFO_RESPONSE_CODE = 0x200002 */
    int64_t code = -1;
    rc = curl_unity_getinfo_long(easy, 0x200002, &code);
    CHECK(rc == CURLE_OK, "getinfo_long(RESPONSE_CODE) rc=%d", rc);
    CHECK(code == 0, "response code should be 0, got %lld", (long long)code);

    /* bridge: getinfo_string — CURLINFO_EFFECTIVE_URL = 0x100001 */
    const char *url = NULL;
    rc = curl_unity_getinfo_string(easy, 0x100001, &url);
    CHECK(rc == CURLE_OK, "getinfo_string(EFFECTIVE_URL) rc=%d", rc);
    CHECK(url != NULL && strstr(url, "example.com") != NULL,
          "effective URL mismatch: %s", url ? url : "(null)");

    curl_easy_cleanup(easy);
}

static void test_multi_handle(void) {
    printf("[multi handle]\n");
    CURLM *multi = curl_multi_init();
    CHECK(multi != NULL, "curl_multi_init returned NULL");
    if (!multi) return;

    /* bridge: multi_setopt_long — CURLMOPT_MAX_TOTAL_CONNECTIONS = 13 */
    int rc = curl_unity_multi_setopt_long(multi, 13, 8);
    CHECK(rc == 0, "multi_setopt_long(MAX_TOTAL_CONNECTIONS) rc=%d", rc);

    /* bridge: multi_info_read on empty multi */
    CURL *out_easy = NULL;
    int out_result = -1;
    int msg = curl_unity_multi_info_read(multi, &out_easy, &out_result);
    CHECK(msg == 0, "multi_info_read should return 0 on empty multi, got %d", msg);

    curl_multi_cleanup(multi);
}

int main(void) {
    printf("=== libcurl_unity smoke test ===\n\n");

    curl_global_init(CURL_GLOBAL_DEFAULT);

    test_version();
    test_easy_handle();
    test_multi_handle();

    curl_global_cleanup();

    printf("\n=== %s (%d failure%s) ===\n",
           failures == 0 ? "PASSED" : "FAILED",
           failures, failures == 1 ? "" : "s");
    return failures > 0 ? 1 : 0;
}
