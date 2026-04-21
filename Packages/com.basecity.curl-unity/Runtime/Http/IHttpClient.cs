using System;
using System.Threading;
using System.Threading.Tasks;

namespace CurlUnity.Http
{
    public interface IHttpClient : IDisposable
    {
        Task<IHttpResponse> SendAsync(IHttpRequest request, CancellationToken ct = default);

        /// <summary>启用代理。对本次调用之后构建的请求生效，不影响已进入 worker 队列的请求。</summary>
        void SetProxy(HttpProxy proxy);

        /// <summary>关闭代理，回到 client 的默认行为（不走代理，且屏蔽环境变量）。</summary>
        void ClearProxy();
    }
}
