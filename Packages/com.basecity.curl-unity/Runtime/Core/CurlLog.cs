using System;
using CurlUnity.Diagnostics;

namespace CurlUnity.Core
{
    /// <summary>
    /// Immutable per-client logger. Filtering happens before entry construction and sink
    /// failures are isolated from all HTTP and cleanup behavior.
    /// </summary>
    internal sealed class CurlLogger
    {
        internal static readonly CurlLogger Default = new CurlLogger(null);

        private readonly CurlLogLevel _level;
        private readonly ICurlLogSink _sink;

        internal CurlLogger(CurlLogOptions options)
        {
            _level = options?.Level ?? CurlLogLevel.Warning;
            _sink = options?.Sink ?? DefaultCurlLogSink.Instance;
        }

        internal bool IsEnabled(CurlLogLevel level)
            => level != CurlLogLevel.Off && level <= _level;

        internal void Error(CurlLogCategory category, string message,
            Exception exception = null, long? requestId = null)
            => Log(CurlLogLevel.Error, category, message, exception, requestId);

        internal void Warning(CurlLogCategory category, string message,
            Exception exception = null, long? requestId = null)
            => Log(CurlLogLevel.Warning, category, message, exception, requestId);

        internal void Verbose(CurlLogCategory category, string message,
            Exception exception = null, long? requestId = null)
            => Log(CurlLogLevel.Verbose, category, message, exception, requestId);

        private void Log(CurlLogLevel level, CurlLogCategory category, string message,
            Exception exception, long? requestId)
        {
            if (!IsEnabled(level)) return;

            var entry = new CurlLogEntry(DateTimeOffset.UtcNow, level, category,
                message, exception, requestId);
            try
            {
                _sink.Write(entry);
            }
            catch
            {
                // Logging is best-effort and must never affect networking or cleanup.
            }
        }
    }

    internal sealed class DefaultCurlLogSink : ICurlLogSink
    {
        internal static readonly DefaultCurlLogSink Instance = new DefaultCurlLogSink();

        private DefaultCurlLogSink() { }

        public void Write(CurlLogEntry entry)
        {
            var request = entry.RequestId.HasValue ? $"[{entry.RequestId.Value}]" : string.Empty;
            var message = $"[curl-unity][{entry.Category}]{request} {entry.Message}";
            if (entry.Exception != null)
                message += Environment.NewLine + entry.Exception;

#if UNITY_5_3_OR_NEWER
            switch (entry.Level)
            {
                case CurlLogLevel.Error:
                    UnityEngine.Debug.LogError(message);
                    break;
                case CurlLogLevel.Warning:
                    UnityEngine.Debug.LogWarning(message);
                    break;
                case CurlLogLevel.Verbose:
                    UnityEngine.Debug.Log(message);
                    break;
            }
#else
            Console.Error.WriteLine($"[{entry.Level}] {message}");
#endif
        }
    }
}
