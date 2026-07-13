# Headers-Complete Callback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fire the public headers callback at the end of the final response header block and move silent SSE connections to `Open` after accepted response headers.

**Architecture:** Add a small response-header-block state machine to `CurlRequest`/`CurlMulti`, driven by libcurl's header callback. Filter informational, redirect, CONNECT, and trailer blocks before creating the early `HttpResponse`; then pass the accepted-headers signal into the SSE connection state machine while retaining byte-based liveness/backoff accounting.

**Tech Stack:** C#/.NET 9 tests, Unity-compatible netstandard2.1 runtime code, libcurl 8.19.0-DEV multi API, xUnit.

## Global Constraints

- Keep `HttpRequest.FollowRedirects` behavior unchanged.
- Do not add a dependency on libcurl private structs or a new native bridge ABI.
- Keep response header capture optional when `EnableResponseHeaders` is false.
- Preserve original callback exception identity.
- Preserve byte-based SSE backoff reset semantics.

---

### Task 1: Header-block observation in the curl core

**Files:**
- Modify: `Packages/com.basecity.curl-unity/Runtime/Native/CurlNative.cs`
- Modify: `Packages/com.basecity.curl-unity/Runtime/Core/CurlRequest.cs`
- Modify: `Packages/com.basecity.curl-unity/Runtime/Core/CurlMulti.cs`
- Modify: `Packages/com.basecity.curl-unity/Runtime/Http/CurlHttpClient.cs`
- Modify: `tests/CurlUnity.UnitTests/TestSupport/FakeCurlApi.cs`
- Test: `tests/CurlUnity.UnitTests/Tests/CurlMultiTests.cs`

**Interfaces:**
- Consumes: `CurlRequest.HeadersReceivedCallback`, `CaptureHeaders`, and request redirect settings.
- Produces: one early `HeadersReceivedCallback(long statusCode, byte[] rawHeaders)` invocation for the final response header block.

- [ ] **Step 1: Write failing core tests**

Add tests that feed status/header/blank lines through `FakeCurlApi.InvokeHeaderCallback` and assert: 200 fires before body, 103 is ignored, followed 302 is ignored until 200, disabled-follow 302 fires, trailer lines do not refire, and callback exceptions return zero while preserving `DownloadError`.

- [ ] **Step 2: Run the focused tests and confirm failure**

Run: `dotnet test tests/CurlUnity.UnitTests/CurlUnity.UnitTests.csproj --filter "FullyQualifiedName~CurlMultiTests"`

Expected: the new header-completion tests fail because `HeadersReceivedCallback` currently fires only from `OnWriteData` or completion.

- [ ] **Step 3: Implement header observation**

Add `CURLOPT_SUPPRESS_CONNECT_HEADERS`, register the header callback when capture or observation is required, store redirect/header-block state on `CurlRequest`, and fire only for a final complete block. Keep first-body and completion fallback paths idempotent through `HeadersReceivedFired`.

- [ ] **Step 4: Run focused core tests**

Run: `dotnet test tests/CurlUnity.UnitTests/CurlUnity.UnitTests.csproj --filter "FullyQualifiedName~CurlMultiTests|FullyQualifiedName~HeadersReceived"`

Expected: all selected tests pass.

### Task 2: SSE opens on accepted headers

**Files:**
- Modify: `Packages/com.basecity.curl-unity/Runtime/Sse/SseCoreExtensions.cs`
- Modify: `Packages/com.basecity.curl-unity/Runtime/Sse/SseConnection.cs`
- Modify: `Packages/com.basecity.curl-unity/Runtime/Sse/SseConnectionState.cs`
- Modify: `tests/CurlUnity.UnitTests/Tests/SseConnectionTests.cs`
- Modify: `tests/CurlUnity.UnitTests/Tests/SseCoreExtensionsTests.cs`
- Modify: `tests/CurlUnity.IntegrationTests/TestServer/TestEndpoints.cs`
- Modify: `tests/CurlUnity.IntegrationTests/Tests/SseTests.cs`

**Interfaces:**
- Consumes: final `HttpRequest.OnHeadersReceived` callbacks from Task 1.
- Produces: `SseConnectionState.Open` after accepted non-204 2xx headers, before body data.

- [ ] **Step 1: Write failing SSE tests**

Update the controllable HTTP client to expose a header gate independently from body data. Assert that accepted 200 headers move a blocked connection to `Open`, non-2xx never opens, 204 closes without opening, and byte delivery still controls backoff reset.

Add a Kestrel endpoint that starts and flushes `text/event-stream` headers, waits, and only then emits data. Assert `OpenSse` reaches `Open` during the silent interval.

- [ ] **Step 2: Run focused SSE tests and confirm failure**

Run: `dotnet test tests/CurlUnity.UnitTests/CurlUnity.UnitTests.csproj --filter "FullyQualifiedName~SseConnectionTests|FullyQualifiedName~SseCoreExtensionsTests"`

Expected: the new open-on-headers assertion fails with `Connecting`.

- [ ] **Step 3: Implement accepted-headers state transition**

Extend `RunOneConnectionAsync` with an internal accepted-headers action. After rejecting non-2xx, call it for non-204 responses. In `SseConnection`, set `Open` and refresh the idle timeout from that action; leave `hadByte`, uptime, parser feed, and backoff reset in `OnByte`.

- [ ] **Step 4: Run focused SSE tests**

Run: `dotnet test tests/CurlUnity.UnitTests/CurlUnity.UnitTests.csproj --filter "FullyQualifiedName~SseConnectionTests|FullyQualifiedName~SseCoreExtensionsTests"`

Expected: all selected unit tests pass.

Run: `dotnet test tests/CurlUnity.IntegrationTests/CurlUnity.IntegrationTests.csproj --filter "FullyQualifiedName~SseTests"`

Expected: all SSE integration tests pass, including the silent-stream test.

### Task 3: Documentation and full verification

**Files:**
- Modify: `Packages/com.basecity.curl-unity/Runtime/Http/HttpRequest.cs`
- Modify: `docs/articles/sse.md`
- Test: all unit and integration projects.

**Interfaces:**
- Consumes: completed behavior from Tasks 1 and 2.
- Produces: accurate public callback and SSE state documentation.

- [ ] **Step 1: Update API documentation**

Document that `OnHeadersReceived` fires after the final response headers, before body, including silent streaming responses. Document that SSE `Open` means accepted response headers rather than first body byte, while `IdleTimeout` still measures body/heartbeat silence.

- [ ] **Step 2: Run full verification**

Run: `dotnet test tests/CurlUnity.UnitTests/CurlUnity.UnitTests.csproj`

Run: `dotnet test tests/CurlUnity.IntegrationTests/CurlUnity.IntegrationTests.csproj`

Run: `dotnet build tests/CurlUnity.IntegrationTests/CurlUnity.IntegrationTests.csproj --configuration Release`

Run: `git diff --check`

Expected: all tests/builds pass; only the repository's known CS0649 warnings may remain; diff check is clean.

- [ ] **Step 3: Commit the implementation**

Stage only the files named in this plan and commit with: `fix(sse): open connection when response headers arrive`.

- [ ] **Step 4: Run independent PR review**

Review the complete branch diff against `origin/master`, resolve every Blocker/Major finding, rerun affected tests, then push and open the PR.
