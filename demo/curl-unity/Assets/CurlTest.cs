using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using CurlUnity.Http;
using CurlUnity.Diagnostics;

public class CurlTest : MonoBehaviour
{
    const int MaxLogLines = 20;

    [SerializeField] private string testUrl = "https://httpbin.org/get";
    [SerializeField] private HttpVersion httpVersion = HttpVersion.PreferH3;
    [SerializeField] private bool enableDiagnostics = true;
    [SerializeField] private bool verifySSL = true;
    [SerializeField] private Text logText;
    [SerializeField] private Button sendButton;

    private CurlHttpClient _http;
    private readonly StringBuilder _log = new();
    private int _logLines;

    void Start()
    {
        _http = new CurlHttpClient(enableDiagnostics)
        {
            PreferredVersion = httpVersion,
            VerifySSL = verifySSL
        };
        Log($"HttpClient 已初始化 (version={httpVersion}, diag={enableDiagnostics}, verifySSL={verifySSL})");
        if (!verifySSL)
            Log("WARNING: SSL 证书验证已关闭，仅用于调试，请勿在正式环境使用");

        if (sendButton != null)
            sendButton.onClick.AddListener(() => _ = PerformGetAsync(testUrl));
    }

    private void OnValidate()
    {
        if (_http == null) return;
        if (_http.PreferredVersion != httpVersion)
        {
            _http.PreferredVersion = httpVersion;
            Log($"更新版本偏好: {httpVersion}");
        }
        if (_http.VerifySSL != verifySSL)
        {
            _http.VerifySSL = verifySSL;
            Log($"更新 VerifySSL: {verifySSL}");
        }
    }

    void OnDestroy()
    {
        _http?.Dispose();
    }

    async Task PerformGetAsync(string url)
    {
        Log($"GET {url} ...");

        try
        {
            using var resp = await _http.GetAsync(url);

            var body = resp.Body != null ? Encoding.UTF8.GetString(resp.Body) : "(no body)";
            Log($"{resp.Version} {resp.StatusCode} [{resp.ContentType}]\n{body}");

            // 单次请求 timing
            if (_http.Diagnostics != null)
            {
                var timing = _http.Diagnostics.GetTiming(resp);
                Log($"Timing: DNS={timing.DnsTimeUs/1000.0:F1}ms Connect={timing.ConnectTimeUs/1000.0:F1}ms " +
                    $"TLS={timing.TlsTimeUs/1000.0:F1}ms TTFB={timing.FirstByteTimeUs/1000.0:F1}ms " +
                    $"Total={timing.TotalTimeUs/1000.0:F1}ms ConnID={timing.ConnectionId} NewConn={timing.NewConnections}");

                // 全局统计
                var snap = _http.Diagnostics.GetSnapshot();
                Log($"Stats: {snap.TotalRequests} reqs, reuse={snap.ConnectionReuseRate:P0}, " +
                    $"avgTTFB={snap.AvgFirstByteTimeUs/1000.0:F1}ms");
            }
        }
        catch (CurlHttpException ex)
        {
            // 网络 / TLS / 超时 / 协议错
            Log($"FAILED [{ex.ErrorKind}] curl({ex.CurlCode}): {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Log("CANCELED");
        }
        catch (Exception ex)
        {
            // 兜底: 不期望到这里;到了就是 bug / 用法错误
            Log($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    void Log(string msg)
    {
        var line = $"[curl] {msg}";
        Debug.Log(line);

        // 环形缓冲：超过 MaxLogLines 行就清空重来，避免 Split 扫描全部 buffer。
        if (_logLines >= MaxLogLines)
        {
            _log.Clear();
            _logLines = 0;
        }
        _log.AppendLine(line);
        _logLines++;

        if (logText != null)
            logText.text = _log.ToString();
    }
}
