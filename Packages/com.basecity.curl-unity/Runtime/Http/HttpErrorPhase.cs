namespace CurlUnity.Http
{
    /// <summary>
    /// 失败发生在请求生命周期的哪一环。与 <see cref="HttpErrorKind"/> 正交:
    /// kind 答"什么错",phase 答"在哪一环"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只列本库能确证的取值。判据是 libcurl 的计时里程碑,而超时路径上只有
    /// <c>TIMER_STARTTRANSFER</c> 可信(<c>sendf.c</c> 的 <c>cw_download_write</c> 在收到首字节时
    /// 直接打点,与连接是否复用无关),所以目前只能确证 <see cref="Transfer"/> 一档。
    /// </para>
    /// <para>
    /// 之所以不列 Dns / Connect / Tls:<c>TIMER_CONNECT</c> / <c>TIMER_APPCONNECT</c> 只由
    /// <c>conn_report_connect_stats</c> 打点,而 multi 接口下超时是在状态机顶部被
    /// <c>multi_handle_timeout</c> 捕获后直接跳出的,这两个点根本不会打——"卡在 TCP 建连"
    /// 和"卡在 TLS 握手"区分不了;<c>TIMER_NAMELOOKUP</c> 本身可信,但复用连接不会打它
    /// (<c>url.c</c> 的 reuse 分支整条跳过 DNS filter),所以"新连接卡在 DNS"和
    /// "复用连接等响应"是同一个签名。这些都只能落到 <see cref="Undefined"/>。
    /// </para>
    /// </remarks>
    public enum HttpErrorPhase
    {
        /// <summary>
        /// 无法判定。含两种情形:里程碑不足以定位(见上),以及本库根本没测量
        /// (setup 阶段失败、easy handle 未移交等)。不要就近归到某个具体环节。
        /// </summary>
        Undefined = 0,

        /// <summary>
        /// 已经收到响应的第一个字节(含响应头),失败发生在传输中。
        /// </summary>
        Transfer,
    }
}
