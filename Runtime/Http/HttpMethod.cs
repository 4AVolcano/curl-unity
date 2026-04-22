namespace CurlUnity.Http
{
    /// <summary>
    /// HTTP 请求方法。对 <c>Get</c> / <c>Post</c> / <c>Head</c> 走 libcurl 原生选项;
    /// 其余走 <c>CURLOPT_CUSTOMREQUEST</c> 填方法名(与 curl 命令行 <c>-X</c> 等价)。
    /// </summary>
    public enum HttpMethod
    {
        /// <summary>GET。无请求体,取回资源。</summary>
        Get,
        /// <summary>POST。提交数据,常见于表单/RPC。</summary>
        Post,
        /// <summary>PUT。幂等上传/替换资源。</summary>
        Put,
        /// <summary>DELETE。删除资源。</summary>
        Delete,
        /// <summary>PATCH。部分更新资源。</summary>
        Patch,
        /// <summary>HEAD。仅取响应头,不返回 body(libcurl 设 <c>CURLOPT_NOBODY=1</c>)。</summary>
        Head,
        /// <summary>OPTIONS。查询资源支持的方法或 CORS preflight。</summary>
        Options
    }
}
