# curl-unity 安全与质量审计报告

**审计日期**：2026-06-11  
**审计范围**：`Packages/com.basecity.curl-unity/`（Runtime 全层）、`scripts/`、`tests/`、`bridge/`  
**修复分支**：`fix/audit-hardening`（基于 `master`，16 个修复提交）  
**测试结果**：单测 43 → 68 全部通过；集成测试 104/106（1 个既有本地 DNS 环境失败，1 个外网 h3 跳过）

---

## 总体结论

架构设计健康，所有权注释、leak-over-crash 策略、SSE 协议实现质量明显高于平均水平。问题集中在「实验室正确、野外脆弱」层面——存在 3 个会直接造成线上崩溃或不可用的阻断项，以及移动端弱网场景下的系统性默认值风险。已全部修复。

---

## 已修复问题

### 阻断级（线上直接崩溃 / 不可用）

#### B1 — P/Invoke 回调 delegate 未持有，Mono 下悬挂函数指针崩溃

**文件**：`Runtime/Core/CurlMulti.cs`  
**提交**：`83cdb72`

`CurlMulti.Send` 把 `OnWriteData`/`OnHeaderData`/`OnReadData` 以方法组直接传给 `SetOpt*Function`。Unity 2022.3（C# 9）不缓存方法组→delegate 转换，每次 Send 生成无人引用的临时 delegate；libcurl 在整个传输期间持有 marshal 出的函数指针。Mono 运行时（Editor / Mono 出包）中 thunk 生命周期绑定 delegate 对象，GC 回收后 libcurl 回调踩悬挂指针 → 随机 native crash（IL2CPP 侥幸安全；.NET 9 测试因 C# 12 缓存方法组也复现不了，导致问题长期潜伏）。

**修复**：改为 `private static readonly` 字段持有，与 `CurlCookieJar` 的既有规范对齐。

---

#### B2 — Android .so 全部 4KB 页对齐，Google Play 2025-11 起会拒绝上架

**文件**：`scripts/build.sh`、`docs/BUILD_GUIDE.md`  
**提交**：`9fe248a`

实测三个 ABI 的 `libcurl_unity.so` 的 ELF PT_LOAD 对齐均为 `0x1000`（4KB）。Android 15+ 的 16KB 页设备上 `dlopen` 直接失败 = App 启动崩溃；Google Play 2025-11 起强制要求 16KB 兼容。更严重的是 arm64 的 `.so.meta` 声明 `Is16KbAligned: true`，会压制 Unity 的对齐告警，把问题静默带上线。

**修复**：`_collect_android` 链接命令加 `-Wl,-z,max-page-size=16384`（NDK r28+ 默认开启，老 NDK 必须显式指定，无条件加上不依赖版本漂移）；链接后用 `llvm-readelf -l` 校验所有 LOAD 段 Align ≥ 0x4000，不满足直接构建失败，杜绝再次静默入库。本地已验证新产物 PT_LOAD Align=0x4000。  
**注意**：Plugins 目录下入库的旧产物仍是 4KB 对齐，需在 CI 重新构建后用修好的 `sync-ci-plugins.sh` 同步入库。

---

#### B3 — worker 线程无顶层异常保护，静默死亡导致所有请求永久悬挂

**文件**：`Runtime/Core/CurlBackgroundWorker.cs`、`Runtime/Core/CurlMulti.cs`  
**提交**：`0b635a8`

`CurlBackgroundWorker.Run` 无任何异常防护：逃逸异常（如 `ProcessCompletion` 对已 Free 的 `GCHandle` 调 `FromIntPtr` 抛 `InvalidOperationException`）会直接杀死线程——所有在飞请求的 Task 永远不完成，后续 Send 入队后无人消费，没有日志、没有恢复、没有失败通知。对长生命周期客户端这是最危险的失败模式（表现为「网络模块莫名全卡死」）。

**修复**：  
- `Run` 加 try/catch：单次异常记 error 后继续跑；连续 5 次失败进入 `faulted` 状态
- faulted 时通过新增的 `CurlMulti.FailAllActive` fail 掉所有在飞请求，并排空队列
- faulted 后 `Send` 快速失败（带根因 inner exception），并处理检查与入队之间的竞态
- `ProcessCompletion` 的 `GCHandle` 解析加防护，解析失败按 stray handle 处理而非逃逸

---

### 高优先级

#### H1 — 默认无超时，移动端弱网请求无限挂起

**文件**：`Runtime/Http/HttpRequest.cs`、`Runtime/Http/IHttpRequest.cs`、`Runtime/Native/CurlNative.cs`、`Runtime/Http/CurlHttpClient.cs`  
**提交**：`22c5b54`

默认 `ConnectTimeoutMs=0`（libcurl 默认 300 秒建连等待）、`TimeoutMs=0`（不限），普通请求无僵死连接检测。Wi-Fi/蜂窝切换、NAT 静默丢弃连接时请求无限挂起——这是复杂客户端环境最高频的故障。

**修复**：`HttpRequest.ConnectTimeoutMs` 默认改为 `30000`（30 秒）；新增请求级低速检测 `LowSpeedLimitBytesPerSecond` / `LowSpeedTimeSeconds`（默认关闭，不影响长下载和 SSE 场景），速率低于阈值持续指定秒数后请求以超时失败；两参数必须成对设置，只设其一 fail-fast。

---

#### H2a — HttpResponse 无 finalizer，忘记 Dispose 永久泄漏 native handle

**文件**：`Runtime/Http/HttpResponse.cs`  
**提交**：`06684c8`

`HttpResponse` 为支持惰性 getinfo 持有 native easy handle，但无任何 finalizer/SafeHandle 兜底。`GetAsync`/`PostJsonAsync` 等所有便利方法返回需手动 Dispose 的对象，异常路径漏 `using` = 永久 native 泄漏，无日志无检测。

**修复**：加 finalizer 兜底 cleanup + 泄漏 warning 日志；构造时 `CurlGlobal.Acquire`、释放时 `Release`，压住 `curl_global_cleanup`，保证 finalizer 的 `easy_cleanup` 不会发生在库卸载之后（UAF）。

---

#### H2b — Diagnostics 强引用 response，开启诊断必然泄漏且 O(n) 遍历

**文件**：`Runtime/Diagnostics/HttpDiagnostics.cs`  
**提交**：`2d109b3`

`_timings` 为 `ConcurrentDictionary<IHttpResponse, ...>`，强引用所有 response：调用方丢弃但未 Dispose 的 response 被字典钉住永不回收，easy handle 永久泄漏（finalizer 安全网也救不了）；100 条后每次 `Record` 都 O(n) 全量遍历。开启诊断 = 必然泄漏。

**修复**：改为 `ConditionalWeakTable<IHttpResponse, StrongBox<HttpRequestTiming>>`，弱引用 key 不阻碍 GC/finalizer，response 被回收后条目自动消失，`Prune` 逻辑完全删除。

---

#### H3 — GET/HEAD + byte[] Body 被 libcurl 静默改写成 POST

**文件**：`Runtime/Http/CurlHttpClient.cs`  
**提交**：`aaf3d43`

只有 `BodyStream` 路径校验了 GET/HEAD 禁带 body；`byte[] Body` 路径无校验，`CURLOPT_COPYPOSTFIELDS` 会把请求方法隐式改成 POST，而 GET 分支又没有 `CUSTOMREQUEST` 兜底——`new HttpRequest { Method = Get, Body = ... }` 实际发出的是 POST，线上排查极难。

**修复**：`Body` 非空 + GET/HEAD 时抛 `InvalidOperationException`，与 `BodyStream` 路径契约对齐。空 `byte[]`（Length==0）不设 POSTFIELDS，维持原行为。

---

#### H4a — 请求 header 无 CR/LF 校验，存在 header 注入面

**文件**：`Runtime/Http/CurlHttpClient.cs`  
**提交**：`06d7231`

header 以 `"{key}: {value}"` 原样拼进 slist，libcurl 对 slist header 不做 CRLF 过滤；header 值（尤其来自服务端下发或用户输入的 token）含 `\r\n` 即可注入任意 header。代理凭据拼接 `"{user}:{pwd}"` 与 `UserAgent` 同理。库内 `MultipartFormData.ValidateContentType` 已有同类防线，但主路径漏掉了。

**修复**：在进入 native 层之前统一校验——header name/value、代理用户名/密码、`UserAgent` 含 CR 或 LF 时抛 `ArgumentException`。

---

#### H4b — 重定向无法关闭，Authorization 跟随跨主机重定向外泄

**文件**：`Runtime/Http/IHttpRequest.cs`、`Runtime/Http/HttpRequest.cs`、`Runtime/Native/CurlNative.cs`、`Runtime/Http/CurlHttpClient.cs`  
**提交**：`962c7ed`

`FOLLOWLOCATION=1` 无条件开启且不可关闭：调用方无法自行处理 3xx（如认证流程），libcurl 跟随时会把自定义 header（含 `Authorization`）原样发给跨主机重定向目标，凭据有外泄面；跳数上限完全依赖 libcurl 版本默认值。

**修复**：`IHttpRequest`/`HttpRequest` 新增 `FollowRedirects`（默认 `true`）与 `MaxRedirects`（默认 30），映射到 `CURLOPT_FOLLOWLOCATION`/`CURLOPT_MAXREDIRS`（常量 68）；`MaxRedirects < -1` fail-fast；接口文档标注跨主机凭据转发的安全提示。

---

#### H5 — macOS 入库 dylib 仅 arm64，meta 声明 OSXUniversal

**文件**：`scripts/sync-ci-plugins.sh`  
**提交**：`7b65859`

入库 `libcurl_unity.dylib` 实测为 arm64 单架构（`lipo -info: Non-fat, arm64`），但 `.meta` 声明 `OSXUniversal/AnyCPU`。CI 早已产出 `macos-arm64` 与 `macos-x86_64` 两个 artifact，但同步脚本只拉 arm64——Intel Mac 玩家或 Intel 编辑器上 `DllNotFoundException`。

**修复**：`sync-ci-plugins.sh` 拉取双架构产物并 `lipo -create` 合成 universal 再入库；x86_64 缺失时显式告警，不再静默。  
**注意**：需在 CI 重新产出双架构产物后重跑脚本。

---

#### H6 — sync-plugins.sh Windows x64 路径错位 + 错误吞掉

**文件**：`scripts/sync-plugins.sh`  
**提交**：`0f2792a`

`build-windows.bat` 按 `OUTPUT_ARCH=x86_64` 写到 `output/Windows/x86_64/`，而 `sync-plugins.sh` 读的是 `output/Windows/x64/`，路径永远不命中；行尾 `2>/dev/null || true` 吞掉所有错误——本地 Windows x64 产物永远静默跳过，Plugins 里残留旧 dll。

**修复**：源路径改为 `x86_64`，去掉两行的错误抑制（`sync_file` 对缺失已有 `[缺失]` 提示）。

---

### 中优先级

#### M1 — HttpResponse getinfo 与 Dispose 并发 TOCTOU use-after-free

**文件**：`Runtime/Http/HttpResponse.cs`  
**提交**：`416b4d8`

`TryGetInfo*` 先判 `_easyHandle == Zero` 再把字段传 native（TOCTOU）：另一线程在间隙 Dispose 后，native 收到已释放 handle → use-after-free。string 类 getinfo 还有第二层窗口：返回的 `char*` 归 easy handle 所有，而 `PtrToStringAnsi` 在锁外解引用时指针已悬挂。

**修复**：新增 `_handleLock`，`TryGetInfo*` 与 `ReleaseHandle` 全程互斥；`TryGetInfoString` 在锁内完成 `PtrToStringAnsi`，指针不再逃逸。新增 4 读线程 × 1000 次并发 Dispose 压力测试。

---

#### M2 — OnWriteData 缓冲路径每 chunk new byte[]，GC 压力高

**文件**：`Runtime/Core/CurlMulti.cs`  
**提交**：`e01a150`

`OnWriteData` 对每个 write 回调都 `new byte[]` 再 `Marshal.Copy`；`OnReadData` 已用 `ArrayPool`，策略不一致。大响应下载期间高频短命分配，移动端 GC 压力明显。

**修复**：缓冲路径（写 `BodyBuffer`）改用 `ArrayPool` 临时缓冲后归还；流式回调路径（`DataCallback`）维持独立数组——契约是用户可安全持有，池化数组归还后内容会被覆盖，不能用。

---

#### M4 — CurlCerts.Initialize 非线程安全，Android 提取失败后无法重试

**文件**：`Runtime/Core/CurlCerts.cs`  
**提交**：`3bad8eb`

`Initialize` 用裸 `bool` 做幂等检查，非线程安全；Android 上 `_initialized = true` 在提取之前置位：JNI 提取失败（如在未 attach JNI 的后台线程构造 client）后 `_caCertPath` 永远为 null，整个进程到重启为止所有 HTTPS 验证全挂，且无恢复手段。

**修复**：双检锁保证只初始化一次；Android 提取失败时不置位，下一个 client 构造时自动重试（日志注明）；`_caCertPath` 加 `volatile` 保证安全发布。

---

#### M5 — Diagnostics 统计口径错误：分母含失败请求，CONN_ID 失败误计

**文件**：`Runtime/Diagnostics/HttpDiagnostics.cs`、`Runtime/Diagnostics/HttpDiagnosticsSnapshot.cs`  
**提交**：`d6b041f`

平均耗时分母用 `_totalRequests`（含失败），但 `_sum*` 只在成功路径累加：失败率 50% 时所有平均耗时被系统性低估一半。`ConnectionReuseRate` 同样把失败计入分母虚高复用率。另外 `CURLINFO_CONN_ID` 读取失败时 out 默认值 0 被当成真实连接 id 收进 `_connIds`，每次读取失败都多计一条假连接。

**修复**：avg 分母、`ConnectionReuseRate` 分母改 `_successRequests`；`CONN_ID` 读取失败标记 `-1`，沿用已有的 `>= 0` 过滤。

---

#### M8 — MultipartFormData.BuildStream 含 Stream part 时允许重复调用

**文件**：`Runtime/Http/MultipartFormData.cs`  
**提交**：`0b5b755`

文档声称「同一 form 可重复调用 BuildStream 产出独立的 stream 对象」，但两次返回的 `MultipartStream` 共享同一组源 `Stream`，第一次消费完源已耗尽，第二次读取必然中途提前 EOF——难排查的 `IOException`，且不可 seek 的源不会被 rewind。

**修复**：含 Stream part 的 form 第二次 `BuildStream` 直接在入口抛 `InvalidOperationException`（提示用新源 Stream 重建 form）；纯内存 part 的 form 维持可重复构建。类级与方法级文档同步修正。

---

## 未修复的遗留问题

以下问题在本次审计中识别，但属于较大范围改动或需要独立决策，未纳入本次修复分支：

| 类别 | 问题 | 备注 |
|---|---|---|
| 测试缺口 | 无句柄/GCHandle 泄漏 soak 断言；client 在飞时 Dispose；h2 多路复用与传输中断注入；真机流水线未入 CI | 需独立 epic |
| 崩溃排障 | Windows 无 PDB；Android 无 build-id、不归档 unstripped .so；macOS 无 dSYM | 提高 native crash 可排查性 |
| 工具链稳定性 | CI 全部 `*-latest` runner，NDK/Xcode/MSVC 版本未 pin | 偶发构建漂移根因 |
| Android minSdk 双轨 | `build.sh` 默认 API 24，CI 用 API 22 | 保持一致 |
| UPM 发布可变性 | `push -f` 可覆盖同名版本，无 checksum manifest | 分发可信度 |
| 连接数上限 | `CURLMOPT_MAX_TOTAL_CONNECTIONS` 等常量已定义但未使用 | API 设计决策 |
| Windows /MD 动态 CRT | 依赖 VC++ Redistributable，与声称的 Win7 支持有落差 | 需权衡 /MT vs /MD |
| macOS 签名/公证 | dylib 仅 ad-hoc 签名，接入方需自行重签；注意事项未在文档说明 | 文档层 |
| CA 缓存感知度 | Android CA 缓存指纹不感知用户增删证书（补丁级更新不触发重提取） | 低风险 |
| SSE 相关问题 | SSE 模块在 `feat/sse-support` 分支，master 无此代码，不在本次范围 | 合并时再处理 |

---

## 测试变化

| | 修复前 | 修复后 |
|---|---|---|
| 单元测试数量 | 43 | 68 |
| 新增测试文件 | — | `CurlBackgroundWorkerTests.cs`、`HttpRequestValidationTests.cs`、`HttpResponseLifetimeTests.cs`、`HttpDiagnosticsTimingTableTests.cs`、`MultipartFormDataTests.cs` |
| 集成测试 | 104/106 | 104/106（同一既有环境失败） |

---

## 提交清单

| 提交 | 类型 | 说明 |
|---|---|---|
| `83cdb72` | fix(core) | 静态缓存 write/header/read 回调 delegate |
| `0b635a8` | fix(core) | worker 线程顶层异常保护 |
| `9fe248a` | fix(build) | Android .so 16KB page 对齐 + 校验门 |
| `aaf3d43` | fix(http) | GET/HEAD + byte[] Body fail-fast |
| `06d7231` | fix(http) | header/凭据/UserAgent CR/LF 注入校验 |
| `962c7ed` | feat(http) | FollowRedirects/MaxRedirects 请求级选项 |
| `22c5b54` | feat(http) | 默认建连超时 30s + 低速僵死检测 |
| `06684c8` | fix(http) | HttpResponse finalizer 泄漏安全网 |
| `2d109b3` | fix(diagnostics) | Diagnostics timing 表改弱引用 |
| `0f2792a` | fix(build) | sync-plugins.sh Windows 路径错位修复 |
| `7b65859` | fix(build) | sync-ci-plugins.sh macOS universal 合成 |
| `416b4d8` | fix(http) | HttpResponse getinfo/Dispose 并发锁 |
| `d6b041f` | fix(diagnostics) | 统计口径修正（分母 + connId） |
| `e01a150` | perf(core) | OnWriteData 缓冲路径 ArrayPool 化 |
| `3bad8eb` | fix(core) | CurlCerts 加锁 + 失败可重试 |
| `0b5b755` | fix(http) | MultipartFormData.BuildStream 二次调用守卫 |
