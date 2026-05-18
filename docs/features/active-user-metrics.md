<!-- freshness:triggers
  src/Humans.Application/Interfaces/IUserActivityTracker.cs
  src/Humans.Infrastructure/Services/UserActivityTracker.cs
  src/Humans.Infrastructure/Services/HumansMetricsService.cs
  src/Humans.Web/Middleware/UserActivityTrackingMiddleware.cs
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Views/Shared/_DashboardStats.cshtml
-->
<!-- freshness:flag-on-change
  Window set (5m/1h/24h), in-memory-only guarantee, middleware pipeline position, and what gets dropped vs. what's a real signal — review when any of these change.
-->

# Active User Metrics

Track distinct authenticated users by trailing window (5m / 1h / 24h) and surface the counts as Prometheus gauges plus three tiles on `/Admin`.

## Business Context

The project owner asked "how many people are using the application" — a basic vital sign for a small nonprofit membership site (~500 users, single-server deployment) that currently has rich operational metrics (consents, suspensions, sync ops) but no signal for human usage.

ASP.NET Core MVC has no Blazor-circuit equivalent. `User.LastLoginAt` is only stamped on sign-in events (Google OAuth callback, magic link) and so misses **cookie reauth** — a user who signed in last week and still has a valid auth cookie never re-stamps it. That makes `LastLoginAt` a sign-in marker, not an activity marker, and unfit for "active users right now".

The constraint shaping the design: containers bounce **daily or more often**. Any state that lives only in process memory loses everything on restart.

## User Stories

### US-AUM.1: Admin sees "is anyone using this right now"
**As an** Admin
**I want** a tile on `/Admin` that shows how many distinct users have hit the site in the last 5 minutes / 1 hour / 24 hours
**So that** I can answer "is the site actually being used" without checking Coolify or grafana
**Acceptance:**
- `/Admin` (any AnyAdminRole policy holder) shows three tiles: Online now (5m), Active (1h), Active (24h)
- Tiles update on page load; no client-side polling
- Counts are distinct users, not request counts

### US-AUM.2: Prometheus scrapes the same numbers
**As an** operator (or future Grafana dashboard)
**I want** `humans.active_users{window="5m|1h|24h"}` exposed at `/metrics`
**So that** I can graph usage over time and alert on "nobody used the site in 24h"
**Acceptance:**
- `curl /metrics | grep humans_active_users` returns three series
- Series share the existing `Humans.Metrics` meter (no new exporter wiring)

### US-AUM.3: 403'd users still count
**As an** operator
**I want** authenticated users who hit an endpoint they're not authorized for (admin-only page as a non-admin) to count as "active"
**So that** the gauge reflects all signed-in traffic, not just authorized traffic
**Acceptance:**
- The middleware runs between `UseAuthentication` and `UseAuthorization` so authorization short-circuiting doesn't suppress the touch

## Data Model

Two new types, no DB schema:

- `IUserActivityTracker` (Application) — `Touch(Guid)` / `CountActiveWithin(Duration)`.
- `UserActivityTracker` (Infrastructure, singleton) — `ConcurrentDictionary<Guid, Instant>` keyed by user id, value is last-seen `Instant` from `IClock`.

No persistence. No EF entity. No table.

## Workflows

**Per-request stamp:**
```
authenticated request → UserActivityTrackingMiddleware (after UseAuthentication, before UseAuthorization)
  → tracker.Touch(userId)
  → continue pipeline
```

**Metric scrape / dashboard render:**
```
Prometheus scrape OR /Admin GET
  → tracker.CountActiveWithin(window) for each of {5m, 1h, 24h}
  → return distinct-user count
```

## Windows: what was dropped and why

The 7d and 30d windows were deliberately not implemented. With daily process restarts, any window longer than current uptime under-reports — a 30-day "active users" right after a restart would be "active in the last few minutes" and would systematically lie. 5m/1h/24h are usable: the worst case is the 24h window briefly capping at uptime after a restart, which is acceptable for a "is anyone here" tile.

If week/month usage trends become important later, the right answer is a small persisted side-table (`UserActivity(UserId PK, LastSeenAt)` owned by metrics, not by `IUserService`) plus a throttled flush — *not* a column on `User`, which would either bust the UserInfo cache on every page load or leave the cached snapshot permanently stale.

## Related Features

- [`docs/sections/admin-shell.md`](../sections/admin-shell.md) — the /Admin dashboard is the consumer of the tiles.
- [`docs/architecture/design-rules.md`](../architecture/design-rules.md) — §15 caching pattern explains why activity stamps can't live on the `User` entity without disrupting the UserInfo snapshot.
- `IHumansMetrics` / `HumansMetricsService` — the existing Prometheus surface this feature plugs into.
