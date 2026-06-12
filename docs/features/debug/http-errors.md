<!-- freshness:triggers
  src/Humans.Application/Interfaces/IClientStatsTracker.cs
  src/Humans.Infrastructure/Services/ClientStatsTracker.cs
  src/Humans.Web/Middleware/ClientStatsMiddleware.cs
  src/Humans.Web/Controllers/DebugController.cs
  src/Humans.Web/Views/Debug/HttpErrors.cshtml
-->
<!-- freshness:flag-on-change
  The >399 capture predicate, the 499-for-aborted convention, the re-execute
  skip (status-code pages) and re-execute record (exception handler), the 429
  OnRejected hook, buffer capacity / truncation bounds, and the
  in-memory/reset-on-restart guarantee — review when any of these change.
-->

# HTTP Errors (Debug)

A `/Debug/HttpErrors` screen showing the last 1000 error responses (status > 399)
with per-request detail: when, code, method, URL, IP, authenticated user, and
classified User-Agent. All in-memory, no DB.

## Business Context

`/Debug/ClientStats` showed ~1000 client errors (400/404/405/499) as bare counts —
the status tally is fed by a `MeterListener` that only ever sees `(code, count)`,
so the errors could not be attributed to bots, broken links, or real users. The
project owner asked for a rolling buffer of the last 1000 such errors, "similar
to `/Debug/Logs` in setup", with who (IP), when, code, URL, and User-Agent.

Same constraint as [client-stats](client-stats.md): the container bounces daily
or more often, so the buffer is "since this deploy", not historical. Acceptable
for a debug aid.

## User Stories

### US-HE.1: Admin attributes error traffic
**As an** Admin
**I want** `/Debug/HttpErrors` to list recent error responses with request detail
**So that** I can tell bot probes from broken links from real user problems
**Acceptance:**
- Every response with status > 399 is captured, across all traffic (pages, assets, API, probes)
- Each row: UTC timestamp, status code, method, URL, IP, authenticated user (when present), classified client label with raw UA as tooltip
- Newest first; `?count=` clamped to 1..1000 (default 1000)
- Per-code lifetime counts (since app start) survive buffer eviction and toggle row visibility
- Linked from the Diagnostics nav and the ClientStats status-code panel

## Design Decisions

- **Storage** — ring buffer (capacity 1000) on the existing `ClientStatsTracker`,
  enqueue-then-trim-under-lock like `InMemoryLogSink`. URL truncated to 200 chars,
  UA to 150; whole buffer stays under ~1 MB. Client label is derived by the
  tracker at record time via `UserAgentClassifier` (Web never calls Infrastructure).
- **Capture point** — `ClientStatsMiddleware` after `await next()`. Three special cases:
  - **Aborted requests record as 499** (nginx convention, matching ASP.NET Core's
    request metric), including aborts that surface as cancellation exceptions.
  - **`UseStatusCodePagesWithReExecute` re-runs are skipped** (`IStatusCodeReExecuteFeature`)
    so each error records once, with the real request path.
  - **`UseExceptionHandler` re-executes are recorded** (that's the only pass that
    completes normally) using `IExceptionHandlerPathFeature.Path` so unhandled
    500s keep the failing URL instead of `/Home/Error`.
- **429s** — the rate limiter rejects before this middleware runs, so the
  limiter's `OnRejected` callback in `Program.cs` records them via the shared
  `ClientStatsMiddleware.BuildEntry`.
- **Known gap (stated on the page)** — requests Kestrel rejects before the
  pipeline (malformed HTTP) never reach middleware, so buffer counts can run
  slightly below the ClientStats meter tally.
- **Not Serilog** — logging each 4xx as a warning would let bot 404 floods evict
  real warnings from the `InMemoryLogSink` buffer; rejected.

## Related Features

- [client-stats](client-stats.md) — same vertical; hosts the status-code tally
  that motivated this buffer and links to this page.
- `docs/features/debug/` siblings; section context in `docs/sections/`.
