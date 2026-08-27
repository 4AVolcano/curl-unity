namespace CurlUnity.Http
{
    /// <summary>
    /// 失败发生在请求生命周期的哪一环。与 <see cref="HttpErrorKind"/> 正交:
    /// kind 答"什么错",phase 答"在哪一环"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transfer 的判断依据</b>:<c>CURLINFO_REDIRECT_COUNT == 0</c> 且
    /// <c>CURLINFO_RESPONSE_CODE &gt; 0</c>。拿到响应状态行即证明连接已建立、请求已发出、
    /// 响应已开始;要求没跟过重定向, 是因为 <c>info.httpcode</c> 存在于 easy handle 而非每一跳——
    /// 跟随重定向时上一跳的状态码会留到下一跳, 下一跳若在拿到状态行之前失败, 读到的是残值。
    /// </para>
    /// <para>
    /// <b>成立条件</b>。以下三条都是 libcurl 的实现事实而非 API 承诺, <b>升级 curl 后必须重新核对</b>:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>info.httpcode</c> 在 HTTP 路径上只由 <c>http.c</c> 的 <c>http_statusline()</c> 赋值,
    ///     即只在解析出合法状态行之后。h2 / h3 也走这里——它们把 <c>:status</c> 合成成
    ///     <c>"HTTP/2 &lt;code&gt;"</c> 形式的状态行再喂给同一个 header parser。
    ///   </description></item>
    ///   <item><description>
    ///     <c>Curl_initinfo()</c> 在每次 transfer 前把 <c>info.httpcode</c> 清零。
    ///   </description></item>
    ///   <item><description>
    ///     <c>state.followlocation</c>(即 <c>CURLINFO_REDIRECT_COUNT</c>)在 <c>http.c</c> 跟随
    ///     重定向时自增, 在 <c>transfer.c</c> 每次 transfer 前清零。
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>升级 curl 后怎么复核</b>:这类变更不体现在 API 上, 只能读源码。在 curl 源码树里跑
    /// <c>grep -rn "info\.httpcode" lib/</c>, 确认 HTTP 路径仍只有 <c>http_statusline()</c> 一处
    /// 写入。前车之鉴:本字段最初用 <c>CURLINFO_STARTTRANSFER_TIME_T</c> 判定, 而 8.21 在
    /// <c>multi.c</c> 的 <c>mstate_enter_completed()</c> 里新增了一处补打——凡是从
    /// <c>MSTATE_DID</c> 之前的状态直接跳到 COMPLETED 的失败(DNS、建连、TLS、建连期超时)都会
    /// 被补上里程碑, 判据当场反向。<c>ErrorPhaseTests</c> 里的
    /// <c>ConnectFailure_LeavesPhaseUndefined</c> 与 <c>Timeout_AfterRedirect_LeavesPhaseUndefined</c>
    /// 就是为这两个坑留的回归防线。
    /// </para>
    /// <para>
    /// 判据是充分不必要条件:没拿到状态行不等于没进传输——请求已发出而服务端迟迟不响应、
    /// 以及跟过重定向的一切失败, 都是这个签名——只是无法确证。宁可少报不错报, 一律落
    /// <see cref="Undefined"/>。
    /// </para>
    /// </remarks>
    public enum HttpErrorPhase
    {
        /// <summary>
        /// 无法判定。含三种情形:没收到响应状态行、本次跟随过重定向(状态码可能是残值)、
        /// 以及本库根本没测量(setup 阶段失败、easy handle 未移交等)。
        /// 不要就近归到某个具体环节。
        /// </summary>
        Undefined = 0,

        /// <summary>
        /// 已经收到响应状态行, 失败发生在传输中。
        /// </summary>
        Transfer,
    }
}
