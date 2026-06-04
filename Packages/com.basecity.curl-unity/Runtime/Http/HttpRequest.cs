using System;
using System.Collections.Generic;
using System.IO;

namespace CurlUnity.Http
{
    public class HttpRequest : IHttpRequest
    {
        public HttpMethod Method { get; set; } = HttpMethod.Get;
        public string Url { get; set; }
        public IEnumerable<KeyValuePair<string, string>> Headers { get; set; }
        public byte[] Body { get; set; }
        public Stream BodyStream { get; set; }
        public long? BodyLength { get; set; }
        public int ConnectTimeoutMs { get; set; } = 30_000;
        public int TimeoutMs { get; set; }
        public int LowSpeedLimitBytesPerSecond { get; set; }
        public int LowSpeedTimeSeconds { get; set; }
        public bool FollowRedirects { get; set; } = true;
        public int MaxRedirects { get; set; } = 30;
        public bool EnableResponseHeaders { get; set; }
        public bool EnableCookies { get; set; }
        public bool AutoDecompressResponse { get; set; } = true;
        public bool TcpNoDelay { get; set; }
        public bool TcpKeepAlive { get; set; }
        public Action<byte[], int, int> OnDataReceived { get; set; }
    }
}
