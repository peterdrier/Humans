<!-- freshness:triggers
  src/Humans.Application/Interfaces/IClientStatsTracker.cs
  src/Humans.Application/Interfaces/IHttpStatusTracker.cs
  src/Humans.Infrastructure/Services/ClientStatsTracker.cs
  src/Humans.Infrastructure/Services/HttpStatusTracker.cs
  src/Humans.Infrastructure/Services/UserAgentClassifier.cs
  src/Humans.Web/Middleware/ClientStatsMiddleware.cs
  src/Humans.Web/Controllers/DebugController.cs
  src/Humans.Web/wwwroot/js/client-metrics.js
-->
<!-- freshness:flag-on-change
  In-memory/reset-on-restart guarantee, the beacon anonymity contract, the
  page-view gate (GET + 2xx/3xx + text/html), cardinality bounds (soft cap),
  and the MeterListener's passive relationship to the OTel export — review when
  any of these change.
-->

# Client Stats (Debug)

A `/Debug/ClientStats` screen showing, since process start, the OS / browser / device-type mix of visitors, their screen-resolution distribution, and the tally of HTTP response status codes. All in-memory, no DB.

## Business Context

The project owner asked "what are people browsing with — Linux/Windows/PC/Mac/phone, resolution — and what status codes are we serving?" as an occasional admin/debug signal, explicitly *not* something to persist. The app already exports rich operational metrics to Prometheus, but there was no in-app, at-a-glance view of client demographics, and the status-code breakdown lived only in Grafana.

Constraint shaping the design (shared with [active-user-metrics](../active-user-metrics.md)): the container bounces **daily or more often**, so anything in process memory is "since this deploy", not historical. That is acceptable for a debug aid.

## User Stories

### US-CS.1: Admin sees the client mix
**As an** Admin
**I want** `/Debug/ClientStats` to show OS, browser, device-type and screen-resolution counts
**So that** I can answer "what do our visitors use" without standing up analytics
**Acceptance:**
- Four ranked tables: Operating system, Browser, Device type, Screen resolution, each with counts and % share
- Counts are since process start; the screen states they reset on restart/redeploy
- OS/browser/device come from the User-Agent; resolution comes from the browser beacon

### US-CS.2: Admin sees the status-code mix
**As an** Admin
**I want** a table of HTTP response status codes (200 vs 403 vs 404 vs 5xx…) with counts
**So that** I can spot a spike in errors at a glance
**Acceptance:**
- One row per observed status code, categorised (Success / Redirect / Client error / Server error)
- Counts cover **all** responses (pages, assets, API, probes) — the screen says so — observed from ASP.NET Core's `http.server.request.duration` instrument

### US-CS.3: Resolution without server-side guessing
**As an** Admin
**I want** screen resolution reported by the browser
**So that** the figure is real (resolution is not in request headers)
**Acceptance:**
- A small script POSTs `{screenWidth, screenHeight}` to `/api/client-metrics` once per browser session (sessionStorage guard), fire-and-forget via `navigator.sendBeacon`
- The endpoint is anonymous and stores **only** the two integers — no UA correlation, no identifiers, no IP retention

## Data Model

Three new types, no DB schema:

- `IClientStatsTracker` (Application) — `RecordPageView(string? ua)`, `RecordResolution(int w, int h)`, `GetSnapshot()`. Impl `ClientStatsTracker` (Infrastructure, singleton): `ConcurrentDictionary` tallies for OS / browser / device / resolution.
- `IHttpStatusTracker` (Application) — `Total`, `GetCounts()`. Impl `HttpStatusTracker` (Infrastructure, singleton + `IHostedService`).
- `UserAgentClassifier` (Infrastructure, static) — maps a UA to coarse `(Os, Browser, Device)` via `MyCSharp.HttpUserAgentParser`.

No persistence, no EF entity, no table.

## Workflows

**Page-view tally:**
```
response produced → ClientStatsMiddleware (after UseAuthorization)
  → only if GET + status 2xx/3xx + text/html        (excludes assets, JSON,
                                                       failed POSTs, re-executed
                                                       error pages)
  → classify UA → bump OS / browser / device counts
```

**Resolution beacon:**
```
first page of a browser session → /js/client-metrics.js
  → sendBeacon POST /api/client-metrics {screenWidth, screenHeight}
  → tracker.RecordResolution (validates 1..16384; soft-capped at 200 buckets + "Other")
```

**Status-code tally:**
```
every request → ASP.NET Core records http.server.request.duration
  → HttpStatusTracker's MeterListener observes the measurement (passive)
  → bump count for the http.response.status_code tag
```

## Design decisions

- **MyCSharp.HttpUserAgentParser, not UAParser.** The canonical `UAParser` NuGet is frozen at 2021 with a stale regex database (misdetects current browsers); MyCSharp is **code-based** (no data file to age), effectively zero-dependency, and actively maintained. Its own OpenTelemetry metrics are operational-only (untagged parse counts) and are left off.
- **Status codes via a passive `MeterListener`, not a second counter.** It observes the same measurements the existing OpenTelemetry→Prometheus exporter consumes, without resetting or interfering with that pipeline. Registered as an `IHostedService` so it counts from the first request. Keyed by status code only (bounded cardinality).
- **Cardinality is bounded.** OS/browser/device labels come from a fixed vocabulary. Resolution buckets are **soft-capped** at 200 (then `Other`); under concurrent first-sightings the cap may be exceeded by a handful before the gate closes — bounded by concurrency, never unbounded.
- **Anonymity.** The beacon carries only screen width/height; it is not joined to the user, the UA, or any identifier.

## What was deliberately left out

- **No persistence / longer windows.** Counts are since-deploy; week/month trends would need a side-table, out of scope for a debug aid.
- **No 5xx detail buffer (yet).** Only counts today. A circular buffer of recent server-error detail is the natural future addition if errors become a concern.
- **No tablet class.** Device type is Mobile / Desktop / Bot — sufficient for "pc/mac/phone".

## Related Features

- [`docs/sections/Debug.md`](../../sections/Debug.md) — the Debug section that owns this screen.
- [`docs/features/active-user-metrics.md`](../active-user-metrics.md) — the sibling in-memory metric (active users); same reset-on-restart constraint.
- `OpenTelemetry` / Prometheus `/metrics` — the existing export the status-code MeterListener rides alongside.
