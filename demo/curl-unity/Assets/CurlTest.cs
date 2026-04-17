using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using CurlUnity.Http;
using CurlUnity.Diagnostics;

public class CurlTest : MonoBehaviour
{
    [SerializeField] private string testUrl = "https://httpbin.org/get";
    [SerializeField] private HttpVersion httpVersion = HttpVersion.PreferH3;
    [SerializeField] private bool enableDiagnostics = true;
    [SerializeField] private Text logText;
    [SerializeField] private Button sendButton;

    private CurlHttpClient _http;
    private readonly StringBuilder _log = new();

    void Start()
    {
        _http = new CurlHttpClient(enableDiagnostics)
        {
            PreferredVersion = httpVersion,
            VerifySSL = false
        };
        Log($"HttpClient 已初始化 (version={httpVersion}, diag={enableDiagnostics})");

        if (sendButton != null)
            sendButton.onClick.AddListener(() => PerformGet(testUrl));
    }

    private void OnValidate()
    {
        if (_http != null && _http.PreferredVersion != httpVersion)
        {
            _http.PreferredVersion = httpVersion;
            Log($"更新版本偏好: {httpVersion}");
        }
    }

    void OnDestroy()
    {
        _http?.Dispose();
    }

    async void PerformGet(string url)
    {
        Log($"GET {url} ...");

        using var resp = await _http.GetAsync(url);

        if (resp.HasResponse)
        {
            var body = resp.Body != null ? Encoding.UTF8.GetString(resp.Body) : "(no body)";
            Log($"{resp.Version} {resp.StatusCode} [{resp.ContentType}]\n{body}");
        }
        else
        {
            Log($"FAILED [{resp.ErrorCode}]: {resp.ErrorMessage}");
        }

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

    void Log(string msg)
    {
        var line = $"[curl] {msg}";
        Debug.Log(line);
        _log.AppendLine(line);
        if (logText != null)
        {
            var str = _log.ToString();
            logText.text = str;
            if (str.Split('\n').Length > 20)
                _log.Clear();
        }
    }
}
