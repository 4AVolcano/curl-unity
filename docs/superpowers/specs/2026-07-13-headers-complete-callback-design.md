# Headers-Complete Callback Design

## Goal

Make `HttpRequest.OnHeadersReceived` fire when the final origin response header block is complete, even when no response body bytes have arrived, and use that signal to move SSE connections to `Open`.

## Constraints

- Keep libcurl automatic redirects enabled.
- Ignore informational responses and proxy CONNECT headers.
- Preserve the same `IHttpResponse` instance between the callback and `SendAsync` completion.
- Preserve callback exception propagation through `SendAsync`.
- Preserve the existing SSE empty-EOF/backoff behavior by keeping body activity separate from the `Open` state.
- Do not depend on private libcurl structures.

## Design

`CurlMulti` registers `CURLOPT_HEADERFUNCTION` whenever response headers are captured or a headers callback exists. It sets `CURLOPT_SUPPRESS_CONNECT_HEADERS` so proxy CONNECT blocks do not enter the managed parser.

`CurlRequest` tracks one response header block: status code, whether it contains `Location`, whether it is a deferred/redirect block, and the final candidate raw headers. `OnHeaderData` starts a block on a synthesized HTTP status line, accumulates lines when requested, and recognizes the terminating blank line.

At a blank line, 1xx blocks are ignored. A 3xx block with `Location` is ignored while automatic redirects are enabled. Other blocks fire `HeadersReceivedCallback` immediately. The existing first-body and transfer-completion paths remain defensive fallbacks for responses that cannot be classified early.

SSE adds an internal accepted-headers callback. A non-204 2xx response moves the connection to `Open` and refreshes the idle timer. Body callbacks only refresh liveness, update byte-based backoff accounting, and feed the parser.

## Error Handling

If the user headers callback throws, `CurlMulti` stores the original exception in `DownloadError` and returns zero from the native header callback. libcurl aborts the transfer, while `CurlHttpClient` rethrows the original exception rather than a generic write error.

## Verification

- Unit-test immediate callback on a completed 200 header block without body.
- Unit-test 1xx and redirect blocks do not trigger before the final response.
- Unit-test disabled redirects expose the 3xx response.
- Unit-test callback-before-body ordering, no-body completion, trailers, and callback exceptions.
- Integration-test an SSE endpoint that flushes headers, stays silent, and reaches `Open` before the first body byte.
- Run unit tests, integration tests, build, and `git diff --check`.
