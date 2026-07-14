using System;

namespace CurlUnity.Diagnostics
{
    /// <summary>Controls how much diagnostic output a curl-unity client emits.</summary>
    public enum CurlLogLevel
    {
        /// <summary>Disable all managed logging.</summary>
        Off = 0,

        /// <summary>Internal invariant or resource-safety failures.</summary>
        Error = 1,

        /// <summary>Recoverable degradation and actionable usage problems.</summary>
        Warning = 2,

        /// <summary>Detailed HTTP and SSE diagnostic flow.</summary>
        Verbose = 3,
    }

    /// <summary>Identifies the curl-unity subsystem that produced a log entry.</summary>
    public enum CurlLogCategory
    {
        Core,
        Http,
        Sse,
        Certificates,
    }

    /// <summary>A structured log entry delivered to <see cref="ICurlLogSink"/>.</summary>
    public readonly struct CurlLogEntry
    {
        public CurlLogEntry(DateTimeOffset timestampUtc, CurlLogLevel level,
            CurlLogCategory category, string message, Exception exception,
            long? requestId)
        {
            TimestampUtc = timestampUtc;
            Level = level;
            Category = category;
            Message = message;
            Exception = exception;
            RequestId = requestId;
        }

        public DateTimeOffset TimestampUtc { get; }
        public CurlLogLevel Level { get; }
        public CurlLogCategory Category { get; }
        public string Message { get; }
        public Exception Exception { get; }
        public long? RequestId { get; }
    }

    /// <summary>Receives log entries emitted by a curl-unity client.</summary>
    /// <remarks>
    /// Calls are synchronous and may come from caller, worker, or finalizer threads.
    /// Implementations must be thread-safe and return quickly.
    /// </remarks>
    public interface ICurlLogSink
    {
        void Write(CurlLogEntry entry);
    }

    /// <summary>Immutable logging configuration associated with an HTTP client instance.</summary>
    public sealed class CurlLogOptions
    {
        public CurlLogOptions(CurlLogLevel level = CurlLogLevel.Warning,
            ICurlLogSink sink = null)
        {
            Level = level;
            Sink = sink;
        }

        /// <summary>The maximum verbosity to emit. Defaults to <see cref="CurlLogLevel.Warning"/>.</summary>
        public CurlLogLevel Level { get; }

        /// <summary>The destination for log entries. Null selects the platform default.</summary>
        public ICurlLogSink Sink { get; }
    }
}
