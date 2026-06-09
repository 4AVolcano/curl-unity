using System;
using System.Collections.Concurrent;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using CurlUnity.Http;
using CurlUnity.Sse;

/// <summary>
/// SSE 测试 Demo：用 Layer 2 <see cref="ISseConnection"/>（OpenSse）连接一个 SSE 端点，
/// 把事件 / 错误 / 状态变化显示到 uGUI。
/// <para>
/// 关键示范：SSE 的回调都在 <b>后台线程</b> 触发（curl-unity 不自动 marshal 主线程），
/// 所以回调里只把数据投递到 <see cref="ConcurrentQueue{T}"/>，由 <see cref="Update"/> 在主线程消费、再碰 Unity UI。
/// </para>
/// </summary>
public class SseTest : MonoBehaviour
{
    const int MaxLogLines = 25;

    [Header("连接")]
    [Tooltip("SSE 端点。httpbingo（httpbin 的 SSE 版）: https://httpbingo.org/sse?count=10&delay=1000ms；" +
             "或真实持续流 Wikimedia: https://stream.wikimedia.org/v2/stream/recentchange")]
    [SerializeField] private string url = "https://httpbingo.org/sse?count=10&delay=1000ms";
    [SerializeField] private HttpVersion httpVersion = HttpVersion.Default;
    [SerializeField] private bool verifySSL = true;

    [Header("重连 / 心跳（Layer 2 策略）")]
    [Tooltip("首次重连延迟（秒），连接成功后重置")]
    [SerializeField] private float reconnectDelayInitSec = 1f;
    [Tooltip("空闲 / 心跳超时（秒）：超过此时长无任何数据则判死重连；0 = 不启用")]
    [SerializeField] private float idleTimeoutSec = 0f;

    [Header("UI")]
    [SerializeField] private Text logText;
    [SerializeField] private Text statusText;
    [SerializeField] private Button connectButton;
    [SerializeField] private Button disconnectButton;

    private CurlHttpClient _http;
    private ISseConnection _sse;

    // 后台回调 → 队列；Update() 在主线程消费
    private readonly ConcurrentQueue<string> _logQueue = new();
    private volatile string _pendingStatus;

    private readonly StringBuilder _log = new();
    private int _logLines;

    void Start()
    {
        _http = new CurlHttpClient
        {
            PreferredVersion = httpVersion,
            VerifySSL = verifySSL,
        };
        if (connectButton != null) connectButton.onClick.AddListener(Connect);
        if (disconnectButton != null) disconnectButton.onClick.AddListener(Disconnect);
        SetStatus("未连接");
        Log("就绪。点 Connect 开始 SSE。");
    }

    public void Connect()
    {
        if (_sse != null) { Log("已连接，先 Disconnect。"); return; }

        var options = new SseConnectionOptions
        {
            ReconnectDelayInit = TimeSpan.FromSeconds(Mathf.Max(0f, reconnectDelayInitSec)),
            IdleTimeout = idleTimeoutSec > 0f ? TimeSpan.FromSeconds(idleTimeoutSec) : (TimeSpan?)null,
        };
        var req = new HttpRequest
        {
            Url = url,
            TimeoutMs = 0, // 长连接不设整体超时（Nagle 默认已关；keep-alive 由 OpenSse 内部开启）
        };

        Log($"OpenSse {url}");
        _sse = _http.OpenSse(req, options);

        // ↓↓↓ 这些回调都在后台线程触发，禁止在此直接碰 Unity API：只投递到队列 ↓↓↓
        _sse.OnEvent += e =>
        {
            var idTag = string.IsNullOrEmpty(e.LastEventId) ? "" : $" (id={e.LastEventId})";
            _logQueue.Enqueue($"[event:{e.EventType}] {Trunc(e.Data, 160)}{idTag}");
        };
        _sse.OnError += ex => _logQueue.Enqueue($"[error] {Describe(ex)}（将自动重连…）");
        _sse.OnStateChanged += (oldS, newS) =>
        {
            _pendingStatus = newS.ToString();
            _logQueue.Enqueue($"[state] {oldS} → {newS}");
        };
    }

    public void Disconnect()
    {
        if (_sse == null) { Log("未连接。"); return; }
        Log("Disconnect");
        _sse.Dispose();   // 取消在飞请求、停止重连、状态置 Closed
        _sse = null;
    }

    void Update()
    {
        // 主线程消费后台回调投递的日志 / 状态
        while (_logQueue.TryDequeue(out var line)) Log(line);
        var s = _pendingStatus;
        if (s != null) { _pendingStatus = null; SetStatus(s); }
    }

    void OnDestroy()
    {
        _sse?.Dispose();
        _http?.Dispose();
    }

    private static string Describe(Exception ex) => ex switch
    {
        SseHttpStatusException s => $"HTTP {s.StatusCode}",
        TimeoutException => "空闲超时",
        CurlHttpException c => $"{c.ErrorKind} (curl {c.CurlCode})",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static string Trunc(string s, int n)
        => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;

    private void SetStatus(string s)
    {
        if (statusText != null) statusText.text = $"状态: {s}";
    }

    private void Log(string msg)
    {
        var line = $"[sse] {msg}";
        Debug.Log(line);
        if (_logLines >= MaxLogLines) { _log.Clear(); _logLines = 0; }
        _log.AppendLine(line);
        _logLines++;
        if (logText != null) logText.text = _log.ToString();
    }
}
