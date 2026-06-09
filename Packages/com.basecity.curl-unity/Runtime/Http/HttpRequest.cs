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
        public int ConnectTimeoutMs { get; set; }
        public int TimeoutMs { get; set; }
        public bool EnableResponseHeaders { get; set; }
        public bool EnableCookies { get; set; }
        public bool AutoDecompressResponse { get; set; } = true;
        public Action<byte[], int, int> OnDataReceived { get; set; }

        /// <summary>
        /// 是否启用 TCP keep-alive（<c>CURLOPT_TCP_KEEPALIVE</c>）。内部字段，不在
        /// <see cref="IHttpRequest"/> 上、不对外暴露；目前仅 SSE 单连接读取（<c>ReadServerSentEventsAsync</c>）
        /// 内部为长连接默认置 true。
        /// </summary>
        internal bool TcpKeepAlive { get; set; }
    }
}
