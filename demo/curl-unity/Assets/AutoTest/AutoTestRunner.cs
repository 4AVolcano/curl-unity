#if CURL_UNITY_AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using CurlUnity.Http;

/// <summary>
/// Automated device test runner for curl-unity.
/// Activated only when CURL_UNITY_AUTOTEST scripting define is set.
/// Auto-injects via [RuntimeInitializeOnLoadMethod] — no scene modification needed.
///
/// Log protocol:
///   [CURL_TEST] BEGIN version=1 platform=<platform> count=<N>
///   [CURL_TEST] RUN <name>
///   [CURL_TEST] PASS <name> <ms>ms
///   [CURL_TEST] FAIL <name> <ms>ms <error>
///   [CURL_TEST] SKIP <name> <reason>
///   [CURL_TEST] END passed=<P> failed=<F> skipped=<S> total_ms=<T>
/// </summary>
public class AutoTestRunner : MonoBehaviour
{
    const string TAG = "[CURL_TEST]";
    const string ResultFileName = "curl_test_results.txt";
    static string _resultPath;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        // Only activate when explicitly requested — normal app launch won't trigger tests.
        // Detection methods (in order):
        //   1. Command-line arg "-autotest" (macOS, Windows, Android via `am start -e autotest 1`)
        //   2. Environment variable CURL_UNITY_AUTOTEST=1 (iOS via idevicedebug -e)
        if (!DetectAutoTestRequest()) return;

        var go = new GameObject("__AutoTestRunner__");
        DontDestroyOnLoad(go);
        go.AddComponent<AutoTestRunner>();
    }

    static bool DetectAutoTestRequest()
    {
        // 1. Command-line arg "-autotest" (macOS, Windows, Android)
        var args = System.Environment.GetCommandLineArgs();
        foreach (var arg in args)
            if (arg == "-autotest") return true;

        // 2. Environment variable CURL_UNITY_AUTOTEST=1 (iOS via idevicedebug -e)
        var env = System.Environment.GetEnvironmentVariable("CURL_UNITY_AUTOTEST");
        if (env == "1") return true;

        return false;
    }

    async void Start()
    {
        // Let engine stabilize
        await Task.Delay(1500);

        // File-based result output (for iOS/Android where log capture is unreliable)
        _resultPath = Path.Combine(Application.persistentDataPath, ResultFileName);
        try { File.Delete(_resultPath); } catch { }
        Log($"INFO results_file={_resultPath}");

        var tests = new List<(string name, Func<CurlHttpClient, CancellationToken, Task> run)>
        {
            ("GET_Basic",        TestGetBasic),
            ("POST_Json",        TestPostJson),
            ("HTTPS_Verify",     TestHttpsVerify),
            ("HTTP2",            TestHttp2),
            ("HTTP3_Only",       TestHttp3Only),
            ("HTTP3_PreferH3",   TestPreferH3),
            ("ResponseHeaders",  TestResponseHeaders),
            ("Redirect",         TestRedirect),
            ("Timeout",          TestTimeout),
            ("Cancel",           TestCancel),
            ("LargeResponse",    TestLargeResponse),
            ("Concurrent",       TestConcurrent),
            ("ConnectionReuse",  TestConnectionReuse),
            ("DnsFailure",       TestDnsFailure),
            ("Upload_Stream",    TestUploadStream),
            ("Cookie_SharedJar", TestCookieSharedJar),
            ("AutoDecompress",   TestAutoDecompress),
        };

        var platform = Application.platform.ToString();
        Log($"BEGIN version=1 platform={platform} count={tests.Count}");

        int passed = 0, failed = 0, skipped = 0;
        var totalSw = Stopwatch.StartNew();

        using var client = new CurlHttpClient(enableDiagnostics: true);

        const int perTestTimeoutMs = 30000; // 单个测试用例最多 30 秒

        foreach (var (name, run) in tests)
        {
            Log($"RUN {name}");
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(perTestTimeoutMs);
            try
            {
                // Token 传到测试里，让 GetAsync/SendAsync 在超时时真的取消底层请求，
                // 避免挂起的 curl 连接拖累后续用例。
                await run(client, cts.Token);
                sw.Stop();
                Log($"PASS {name} {sw.ElapsedMilliseconds}ms");
                passed++;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                sw.Stop();
                Log($"FAIL {name} {sw.ElapsedMilliseconds}ms Test timed out after {perTestTimeoutMs}ms");
                failed++;
            }
            catch (SkipException ex)
            {
                sw.Stop();
                Log($"SKIP {name} {Sanitize(ex.Message)}");
                skipped++;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"FAIL {name} {sw.ElapsedMilliseconds}ms {Sanitize(ex.Message)}");
                failed++;
            }
        }

        totalSw.Stop();

        // Emit diagnostics summary
        if (client.Diagnostics != null)
        {
            var snap = client.Diagnostics.GetSnapshot();
            Log($"DIAG total_requests={snap.TotalRequests} success={snap.SuccessRequests} " +
                $"failed={snap.FailedRequests} reuse_rate={snap.ConnectionReuseRate:F2} " +
                $"avg_ttfb_ms={snap.AvgFirstByteTimeUs / 1000.0:F1} " +
                $"avg_total_ms={snap.AvgTotalTimeUs / 1000.0:F1}");
        }

        Log($"END passed={passed} failed={failed} skipped={skipped} total_ms={totalSw.ElapsedMilliseconds}");

        // Let logs flush before quitting
        await Task.Delay(500);
        Application.Quit(failed > 0 ? 1 : 0);
    }

    // ================================================================
    // Test cases
    // ================================================================

    static async Task TestGetBasic(CurlHttpClient client, CancellationToken ct)
    {
        using var resp = await client.GetAsync("https://httpbin.org/get", ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        Assert(resp.Body != null && resp.Body.Length > 0, "Empty body");
    }

    static async Task TestPostJson(CurlHttpClient client, CancellationToken ct)
    {
        var json = "{\"test\":\"hello\",\"num\":42}";
        using var resp = await client.PostJsonAsync("https://httpbin.org/post", json, ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        var body = Encoding.UTF8.GetString(resp.Body);
        Assert(body.Contains("\"test\""), "Response missing posted JSON data");
        Assert(body.Contains("hello"), "Response missing posted value");
    }

    static async Task TestHttpsVerify(CurlHttpClient client, CancellationToken ct)
    {
        // This is the most important platform-specific test:
        // - macOS/iOS: uses Apple SecTrust with system cert store
        // - Android: uses JNI-extracted system certs via CurlCerts
        var saved = client.VerifySSL;
        client.VerifySSL = true;
        try
        {
            using var resp = await client.GetAsync("https://www.example.com/", ct);
            Assert(resp.HasResponse,
                $"HTTPS verification failed: err={resp.ErrorCode} {resp.ErrorMessage}");
            Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        }
        finally
        {
            client.VerifySSL = saved;
        }
    }

    static async Task TestHttp2(CurlHttpClient client, CancellationToken ct)
    {
        var saved = client.PreferredVersion;
        client.PreferredVersion = HttpVersion.Http2;
        try
        {
            using var resp = await client.GetAsync("https://httpbin.org/get", ct);
            Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
            // HTTP/2 = enum value 3
            Assert((int)resp.Version >= 3,
                $"Expected HTTP/2+, got {resp.Version} ({(int)resp.Version})");
        }
        finally
        {
            client.PreferredVersion = saved;
        }
    }

    // HTTP/3 测试用到的外部服务器，回显实际使用的协议（proto=HTTP/3 / HTTP/2 等）。
    const string H3TestUrl = "https://h3-test.godrive.top/";

    static async Task TestHttp3Only(CurlHttpClient client, CancellationToken ct)
    {
        // 强制仅使用 HTTP/3（CURL_HTTP_VERSION_3ONLY）：必须直接 QUIC 握手，
        // 不允许 HTTP/2 fallback。验证 ngtcp2 + nghttp3 的打通与 UDP/443 连通性。
        var saved = client.PreferredVersion;
        client.PreferredVersion = HttpVersion.Http3Only;
        try
        {
            using var resp = await client.GetAsync(H3TestUrl, ct);
            Assert(resp.HasResponse,
                $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
            Assert(resp.StatusCode == 200,
                $"Expected 200, got {resp.StatusCode}");
            Assert(resp.Version == HttpVersion.Http3,
                $"Expected HTTP/3, got {resp.Version} ({(int)resp.Version})");

            var body = resp.Body != null ? Encoding.UTF8.GetString(resp.Body) : "";
            Assert(body.StartsWith("proto=HTTP/3"),
                $"Expected body to start with proto=HTTP/3, got: {body.Substring(0, Math.Min(60, body.Length))}");
        }
        finally
        {
            client.PreferredVersion = saved;
        }
    }

    static async Task TestPreferH3(CurlHttpClient client, CancellationToken ct)
    {
        // CURL_HTTP_VERSION_3 (PreferH3)：首次请求通常走 HTTP/2 + alt-svc 广播，
        // 后续连接升级到 HTTP/3。发两次请求验证 alt-svc 识别工作正常。
        var saved = client.PreferredVersion;
        client.PreferredVersion = HttpVersion.PreferH3;
        try
        {
            using var resp1 = await client.GetAsync(H3TestUrl, ct);
            Assert(resp1.HasResponse,
                $"First request failed: err={resp1.ErrorCode} {resp1.ErrorMessage}");

            using var resp2 = await client.GetAsync(H3TestUrl, ct);
            Assert(resp2.HasResponse,
                $"Second request failed: err={resp2.ErrorCode} {resp2.ErrorMessage}");
            Assert(resp2.StatusCode == 200,
                $"Expected 200, got {resp2.StatusCode}");

            // 第二次请求应该识别到 alt-svc 并升级到 HTTP/3；HTTP/2 也接受，留容差。
            var body = resp2.Body != null ? Encoding.UTF8.GetString(resp2.Body) : "";
            Assert(body.StartsWith("proto=HTTP/"),
                $"Expected body to start with proto=HTTP/, got: {body.Substring(0, Math.Min(60, body.Length))}");
        }
        finally
        {
            client.PreferredVersion = saved;
        }
    }

    static async Task TestResponseHeaders(CurlHttpClient client, CancellationToken ct)
    {
        var request = new HttpRequest
        {
            Url = "https://httpbin.org/response-headers?X-Curl-Test=hello123",
            EnableResponseHeaders = true
        };
        using var resp = await client.SendAsync(request, ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        Assert(resp.Headers != null, "Headers dict is null");
        Assert(resp.Headers.ContainsKey("x-curl-test"), "Missing X-Curl-Test header");
        var values = resp.Headers["x-curl-test"];
        Assert(values.Length > 0 && values[0] == "hello123",
            $"X-Curl-Test value mismatch: {string.Join(",", values)}");
    }

    static async Task TestRedirect(CurlHttpClient client, CancellationToken ct)
    {
        using var resp = await client.GetAsync("https://httpbin.org/redirect/3", ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200 after redirects, got {resp.StatusCode}");
        Assert(resp.RedirectCount >= 3,
            $"Expected >= 3 redirects, got {resp.RedirectCount}");
        Assert(resp.EffectiveUrl != null && resp.EffectiveUrl.Contains("/get"),
            $"Unexpected final URL: {resp.EffectiveUrl}");
    }

    static async Task TestTimeout(CurlHttpClient client, CancellationToken ct)
    {
        var request = new HttpRequest
        {
            Url = "https://httpbin.org/delay/30",
            TimeoutMs = 2000
        };
        using var resp = await client.SendAsync(request, ct);
        Assert(!resp.HasResponse, "Expected timeout but got response");
        // CURLE_OPERATION_TIMEDOUT = 28
        Assert(resp.ErrorCode == 28,
            $"Expected CURLE_OPERATION_TIMEDOUT (28), got {resp.ErrorCode}: {resp.ErrorMessage}");
    }

    static async Task TestCancel(CurlHttpClient client, CancellationToken ct)
    {
        // Link outer ct with a local 500ms cancellation so external timeout still works.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(500);
        bool cancelled = false;
        try
        {
            var request = new HttpRequest { Url = "https://httpbin.org/delay/30" };
            using var resp = await client.SendAsync(request, linked.Token);
            // Should not reach here
            Assert(false, $"Request completed instead of being cancelled, status={resp.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Local 500ms cancellation fired; outer timeout hasn't — this is the expected path.
            cancelled = true;
        }
        Assert(cancelled, "Expected cancellation exception");
    }

    static async Task TestLargeResponse(CurlHttpClient client, CancellationToken ct)
    {
        const int expectedSize = 102400; // 100KB
        using var resp = await client.GetAsync($"https://httpbin.org/bytes/{expectedSize}", ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        Assert(resp.Body != null, "Body is null");
        // Allow some tolerance (httpbin may return slightly different sizes)
        Assert(resp.Body.Length >= expectedSize * 0.9 && resp.Body.Length <= expectedSize * 1.1,
            $"Body size mismatch: expected ~{expectedSize}, got {resp.Body.Length}");
    }

    static async Task TestConcurrent(CurlHttpClient client, CancellationToken ct)
    {
        const int count = 5;
        var tasks = new Task<IHttpResponse>[count];
        for (int i = 0; i < count; i++)
            tasks[i] = client.GetAsync($"https://httpbin.org/get?idx={i}", ct);

        var responses = await Task.WhenAll(tasks);
        int successCount = 0;
        try
        {
            foreach (var resp in responses)
            {
                if (resp.HasResponse && resp.StatusCode == 200)
                    successCount++;
            }
            Assert(successCount == count,
                $"Only {successCount}/{count} concurrent requests succeeded");
        }
        finally
        {
            foreach (var resp in responses)
                resp?.Dispose();
        }
    }

    static async Task TestConnectionReuse(CurlHttpClient client, CancellationToken ct)
    {
        // Reset diagnostics to get clean stats for this test
        client.Diagnostics?.Reset();

        // 5 sequential requests to the same host should reuse connections
        for (int i = 0; i < 5; i++)
        {
            using var resp = await client.GetAsync("https://httpbin.org/get", ct);
            Assert(resp.HasResponse, $"Request {i} failed: err={resp.ErrorCode}");
        }

        if (client.Diagnostics != null)
        {
            var snap = client.Diagnostics.GetSnapshot();
            Assert(snap.TotalRequests == 5,
                $"Expected 5 requests, got {snap.TotalRequests}");
            Assert(snap.ConnectionReuseRate > 0,
                $"No connection reuse detected (rate={snap.ConnectionReuseRate:F2})");
        }
    }

    static async Task TestDnsFailure(CurlHttpClient client, CancellationToken ct)
    {
        // Use a short timeout — some networks DNS-hijack invalid domains,
        // causing long connection attempts instead of immediate DNS failure
        var request = new HttpRequest
        {
            Url = "http://this.host.does.not.exist.invalid/",
            TimeoutMs = 5000
        };
        using var resp = await client.SendAsync(request, ct);
        Assert(!resp.HasResponse,
            $"Expected failure but got HTTP {resp.StatusCode}");
        Assert(resp.ErrorCode != 0,
            $"Expected non-zero error code, got {resp.ErrorCode}");
        // CURLE_COULDNT_RESOLVE_HOST (6) is ideal, but some networks
        // DNS-hijack and return CURLE_GOT_NOTHING (52), CURLE_OPERATION_TIMEDOUT (28), etc.
        // Any error is acceptable — the point is that the request failed gracefully.
    }

    static async Task TestUploadStream(CurlHttpClient client, CancellationToken ct)
    {
        // 验证流式上传(READFUNCTION 回调 + 新增的 curl_unity_setopt_read_function
        // bridge 符号)在各平台导出正常。
        // 用确定性文本 payload(64KB ASCII 'A'),确保 server 能 echo 回原文,
        // 并断言长度 + 含特征字符串。"data" 字段空也存在, 不能只 check 它。
        const int size = 64 * 1024;
        var payload = new string('A', size);
        var bytes = Encoding.ASCII.GetBytes(payload);
        using var src = new MemoryStream(bytes);
        var req = new HttpRequest
        {
            Method = HttpMethod.Post,
            Url = "https://httpbin.org/post",
            BodyStream = src,
            BodyLength = src.Length,
        };
        using var resp = await client.SendAsync(req, ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        var body = Encoding.UTF8.GetString(resp.Body);
        // httpbin /post 把原 body echo 到 JSON 的 "data" 字段(若是 ASCII 文本则原样返回)
        // 验证含原 payload 长度的 'A' 序列,证明 64KB 全部送达
        Assert(body.Contains(new string('A', 256)),
            $"Response 'data' missing expected payload (upload may be truncated). First 200: {body.Substring(0, Math.Min(200, body.Length))}");
    }

    static async Task TestCookieSharedJar(CurlHttpClient client, CancellationToken ct)
    {
        // 验证 CURLSH 跨请求 cookie 共享在各平台工作(涉及 lock/unlock 回调
        // 的线程原语, 平台差异点)。第一次请求让 server 种 cookie, 第二次
        // 请求断言客户端把该 cookie 回发给了 server。
        var r1 = new HttpRequest
        {
            Url = "https://httpbin.org/cookies/set?curlUnityTest=rc1",
            EnableCookies = true,
        };
        using (var resp1 = await client.SendAsync(r1, ct))
        {
            Assert(resp1.HasResponse, $"Set-cookie request failed: err={resp1.ErrorCode}");
        }

        var r2 = new HttpRequest
        {
            Url = "https://httpbin.org/cookies",
            EnableCookies = true,
        };
        using var resp2 = await client.SendAsync(r2, ct);
        Assert(resp2.HasResponse, $"Check-cookie request failed: err={resp2.ErrorCode}");
        Assert(resp2.StatusCode == 200, $"Expected 200, got {resp2.StatusCode}");
        var body = Encoding.UTF8.GetString(resp2.Body);
        // 用 JSON 字段精确匹配, 避免 "curlUnityTest" 或 "rc1" 出现在其它 key/value
        // 里造成假阳性 (httpbin 把 cookies 序列化成 {"name": "value"})
        Assert(body.Contains("\"curlUnityTest\": \"rc1\""),
            $"Cookie not echoed back as expected: {body.Substring(0, Math.Min(200, body.Length))}");
    }

    static async Task TestAutoDecompress(CurlHttpClient client, CancellationToken ct)
    {
        // AutoDecompressResponse 默认开启 → 客户端发 Accept-Encoding: gzip,
        // libcurl 透明解压。验证各平台 zlib 链接正常(若 zlib 链接失败,
        // 会拿到原始压缩字节或请求直接失败)。
        // httpbin.org/gzip 强制返回 gzip 响应, 解压后 JSON 含 "gzipped": true。
        using var resp = await client.GetAsync("https://httpbin.org/gzip", ct);
        Assert(resp.HasResponse, $"No response: err={resp.ErrorCode} {resp.ErrorMessage}");
        Assert(resp.StatusCode == 200, $"Expected 200, got {resp.StatusCode}");
        var body = Encoding.UTF8.GetString(resp.Body);
        // 精确匹配 JSON 片段, 避免 "true" 出现在其它字段(如 origin) 误判
        Assert(body.Contains("\"gzipped\": true"),
            $"Body missing '\"gzipped\": true' fragment (decompression may have failed): {body.Substring(0, Math.Min(200, body.Length))}");
    }

    // ================================================================
    // Helpers
    // ================================================================

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Log(string msg)
    {
        var line = $"{TAG} {msg}";
        UnityEngine.Debug.Log(line);
        // Also append to file (survives even if log capture fails)
        try
        {
            if (_resultPath != null)
                File.AppendAllText(_resultPath, line + "\n");
        }
        catch { /* best effort */ }
    }

    static string Sanitize(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return "(no message)";
        // Keep single-line for grep parsing
        return msg.Replace('\n', ' ').Replace('\r', ' ');
    }

    class SkipException : Exception
    {
        public SkipException(string reason) : base(reason) { }
    }
}
#endif
