# Mailer Section — Outbound Slice + Audience Framework (Slice 2)

**Status:** Design — awaiting user approval before plan.
**Author / facilitator:** brainstorming session 2026-05-14.
**Related:** [`docs/sections/Mailer.md`](../../sections/Mailer.md) (inbound-only invariant to be relaxed), [`docs/superpowers/specs/2026-05-12-mailer-section-inbound-import-design.md`](2026-05-12-mailer-section-inbound-import-design.md) (Slice 1).

## 1. Context

Slice 1 (inbound-only) shipped 2026-05-12. Today's slice flips the long-standing "outbound is the next slice and must ship before any other Mailer feature" invariant from a TODO into reality, and lays down an extensible **audience framework** on top — Humans-defined, code-registered mailing lists whose membership is computed from Humans state and synced into named ML groups.

The first audience is **"Humans - Ticket no Shifts"** — humans who hold a valid ticket but have not signed up for any shift in the active event. Future audiences are anticipated (per-language splits like "Humans - General - DE", and a handful of other segments) but only the framework + first audience are in scope here.

## 2. Scope

### In scope

- Outbound writes on `IMailerLiteService`: create group, assign subscriber to group, unassign, bulk-import-to-group.
- Prefix-write invariant: writes only target ML groups whose `name` starts with `"Humans - "`. Enforced at runtime in the client and pinned by architecture test.
- `IMailerAudience` primitive — code-registered audience definitions.
- One audience implementation: `TicketNoShiftsAudience`.
- New service `MailerAudienceSyncService` (orchestrator: compute → diff → apply → audit).
- New Hangfire job `MailerAudienceSyncJob` (daily, configurable cron, default low-traffic Madrid morning).
- On-demand "Push Now" button per audience on `/Mailer/Admin`.
- Audiences card on `/Mailer/Admin` dashboard with per-audience stats.
- New `AuditAction.MailerLiteAudienceSyncCompleted`.
- Tests: audience definition, sync orchestration, idempotency, architecture pins, controller smoke.
- Section-doc update: relax `IMailerLiteService` GET-only invariant; add prefix-write invariant; document the audience-framework concept.

### Out of scope

- CRUD UI for audiences (audiences are code-registered; adding one is a PR).
- Other audiences besides "Ticket no Shifts" (language splits etc. follow in their own PRs once the framework lands).
- Webhooks from ML to Humans (Slice 3+ territory).
- Engagement metrics (open/click rates).
- Two-way drift reconciliation beyond what Slice 1 already does (we still treat ML as the source of truth for subscriber `status`).
- Removing a subscriber from ML entirely (only group membership is managed here).

## 3. Architecture

### Outbound slice on `IMailerLiteService`

The interface gains four write methods. The client (`MailerLiteClient`) runtime-guards every write against the `"Humans - "` group-name prefix; an architecture test pins the set of allowed write methods and the prefix check.

```csharp
public interface IMailerLiteService : IApplicationService
{
    // existing reads (unchanged)
    Task<MailerLiteAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(CancellationToken ct = default);
    IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(CancellationToken ct = default);
    Task<MailerLiteSubscriber?> GetSubscriberAsync(string email, CancellationToken ct = default);

    // new — outbound writes (prefix-guarded)
    Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default);
    Task AssignSubscriberToGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);
    Task UnassignSubscriberFromGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);
    Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
        string groupId, IReadOnlyList<string> emails, CancellationToken ct = default);
}
```

**Runtime guard** (in `MailerLiteClient`): every write method first looks up the group by id (or, in `CreateGroupAsync`, inspects the requested `name`) and throws `InvalidOperationException` before the HTTP call if the name doesn't start with `"Humans - "`. The existing GET-only `SendAsync` guard is removed and replaced with per-method write paths that share the same group-name verification helper.

**MailerLite API notes for implementer to verify against current v2 docs:**

- ML v2 has **no "set membership to exactly this set"** endpoint — adds and removes are computed by diff and applied individually (or bulk on the add side).
- **Add path**: `POST /api/groups/{group_id}/subscribers/import` accepts an array of subscriber objects per call (documented max believed ~50 — implementer to confirm exact ceiling). Used for `BulkImportSubscribersToGroupAsync` (chunked).
- **Single-assign fallback**: `POST /api/subscribers/{id_or_email}/groups/{group_id}`. Used by `AssignSubscriberToGroupAsync` and as a fallback if the bulk import endpoint is not available.
- **Remove path**: `DELETE /api/subscribers/{id}/groups/{group_id}`, one call per subscriber. (No documented bulk remove in v2.)
- **Group create**: `POST /api/groups` with `{ "name": "..." }`.

Implementer fetches current ML v2 docs before writing `MailerLiteClient` and adjusts the bulk shape (max chunk size, exact endpoint path, request body) to whatever's current. The four interface methods are the stable contract; their HTTP shape is implementation detail.

### Audience framework (new)

```
src/Humans.Application/Interfaces/Mailer/
  IMailerAudience.cs                ← audience primitive
  IMailerAudienceSyncService.cs     ← orchestrator
  Dtos/
    AudienceSyncResult.cs           ← per-audience post-sync counts
    AudienceStats.cs                ← computed-but-not-yet-pushed counts for dashboard

src/Humans.Application/Services/Mailer/
  MailerAudienceSyncService.cs      ← orchestrator impl; Application layer; no DbContext
  Audiences/
    TicketNoShiftsAudience.cs       ← first IMailerAudience impl

src/Humans.Infrastructure/Jobs/
  MailerAudienceSyncJob.cs          ← Hangfire recurring job

src/Humans.Web/Controllers/Mailer/
  MailerAdminController.cs          ← extended with POST /Mailer/Admin/Audiences/{key}/Sync

src/Humans.Web/Models/Mailer/
  AudiencesCardViewModel.cs         ← per-audience stats for the dashboard card

src/Humans.Web/Views/Mailer/Admin/
  Index.cshtml                      ← extended with Audiences card
  _AudiencesCard.cshtml             ← new partial (one row per registered audience)
```

### `IMailerAudience` primitive

```csharp
public interface IMailerAudience
{
    /// <summary>Stable key used in the audience-sync URL (e.g. "ticket-no-shifts").</summary>
    string Key { get; }

    /// <summary>Display name shown on the dashboard.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Target ML group name. Must start with "Humans - " — enforced by
    /// MailerArchitectureTests.AllAudiences_UseHumansPrefix.
    /// </summary>
    string MailerLiteGroupName { get; }

    /// <summary>
    /// Computes the set of Humans user-ids who currently belong in this audience.
    /// Implementation reads cross-section state via service interfaces only — never DbContext.
    /// </summary>
    Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct);
}
```

Audiences are registered as singletons in DI: `services.AddSingleton<IMailerAudience, TicketNoShiftsAudience>();`. The framework discovers all of them via `IEnumerable<IMailerAudience>` injection.

### `TicketNoShiftsAudience`

```csharp
public sealed class TicketNoShiftsAudience(
    ITicketQueryService tickets,
    IShiftSignupService shiftSignups,
    IShiftManagementService shiftManagement) : IMailerAudience
{
    public string Key => "ticket-no-shifts";
    public string DisplayName => "Ticket holders without a shift";
    public string MailerLiteGroupName => "Humans - Ticket no Shifts";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var activeEvent = await shiftManagement.GetActiveAsync(ct);
        if (activeEvent is null) return new HashSet<Guid>();

        var ticketHolders = await tickets.GetUserIdsWithMatchedAttendeesAsync(ct);
        // Returns userIds with at least one Valid|CheckedIn matched TicketAttendee
        // in the active vendor event. Buyer-only matches excluded — matches the
        // existing "has a ticket" definition pinned by Tickets.md.

        var shiftHaverUserIds = await shiftSignups.GetActiveCommittedUserIdsForEventAsync(
            activeEvent.Id, ct);
        // Returns userIds with at least one ShiftSignup whose Status ∈ {Pending, Confirmed}
        // for the active event.

        var audience = new HashSet<Guid>(ticketHolders);
        audience.ExceptWith(shiftHaverUserIds);
        return audience;
    }
}
```

Two read methods are added to existing section services:

- `ITicketQueryService.GetUserIdsWithMatchedAttendeesAsync(ct)` — already exists in spirit as `GetAllMatchedUserIdsAsync` (filtered to attendees, status Valid|CheckedIn); confirm name during implementation.
- `IShiftSignupService.GetActiveCommittedUserIdsForEventAsync(eventSettingsId, ct)` — new narrow read; routes through `IShiftSignupRepository`.

### `MailerAudienceSyncService`

```csharp
public interface IMailerAudienceSyncService : IApplicationService
{
    Task<AudienceStats> ComputeStatsAsync(IMailerAudience audience, CancellationToken ct);
    Task<AudienceSyncResult> SyncAsync(IMailerAudience audience, CancellationToken ct);
    Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(CancellationToken ct);
}
```

**`ComputeStatsAsync`** — read-only, no ML writes. Used by the dashboard card to display the headline numbers without forcing a sync:
- `Candidates` = `audience.ComputeMemberUserIdsAsync().Count`
- `ExcludedUnsubscribed` = candidates whose primary email maps to an ML subscriber with `status ∈ {unsubscribed, bounced, junk}`
- `CurrentlyInGroup` = ML subscribers currently in the target group (or 0 if group doesn't exist yet)
- `LastSyncAt`, `LastSyncSummary` — from the most recent `MailerLiteAudienceSyncCompleted` audit entry for this audience key

**`SyncAsync`** — full data flow:

1. `memberUserIds = audience.ComputeMemberUserIdsAsync(ct)`
2. `userEmailMap = IUserEmailService.GetPrimaryEmailsByUserIdsAsync(memberUserIds)` — drop users with no primary email, log count.
3. **Ensure ML group exists.** `groups = _ml.ListGroupsAsync()`; find by `Name == audience.MailerLiteGroupName`; create if missing (prefix-guarded).
4. **Snapshot ML state.** One paginated sweep of `_ml.ListSubscribersAsync()` → `byEmail: Dict<normalized-email, MailerLiteSubscriber>`. From each subscriber's `Groups` collection, build `currentGroupMemberIds: Set<subscriberId>` for the target group.
5. **Classify candidates.** For each `(userId, email)`:
   - `normalized = NormalizingEmailComparer.Normalize(email)`
   - `sub = byEmail.TryGet(normalized)`
   - `sub?.Status ∈ {unsubscribed, bounced, junk}` → bucket `ExcludedUnsubscribed`
   - `sub is null` → bucket `ToCreateAndAssign` (uses bulk import)
   - `sub.Id ∈ currentGroupMemberIds` → bucket `AlreadyAssigned`
   - else → bucket `ToAssign`
6. **Compute removals.** For each `subId ∈ currentGroupMemberIds` whose subscriber email's user-id is NOT in `memberUserIds` (or maps to no Humans user at all) → bucket `ToUnassign`.
7. **Apply.**
   - `ToCreateAndAssign` → `_ml.BulkImportSubscribersToGroupAsync(group.Id, chunk, ct)`, chunked per ML's documented batch ceiling.
   - `ToAssign` → `_ml.AssignSubscriberToGroupAsync(sub.Id, group.Id, ct)` per subscriber.
   - `ToUnassign` → `_ml.UnassignSubscriberFromGroupAsync(sub.Id, group.Id, ct)` per subscriber.
   - Each individual ML call wrapped in try/catch; failures logged + counted in `errors`; sync continues.
8. **Audit.** Write one `AuditAction.MailerLiteAudienceSyncCompleted` entry with metadata `{ audience_key, group_id, candidates, excluded_unsubscribed, created, assigned, already_assigned, unassigned, errors }`. No per-row audit entries (matches the existing inbound-import pattern's idempotent-summary shape).

**Idempotency:** a second immediate run produces all-zero deltas except `AlreadyAssigned`. The audit is still written (one summary entry) but its counts are all zero.

**Failure semantics:** any individual ML write that fails does NOT roll back the sync. Partial progress is fine; the next run reconciles. The summary audit surfaces the error count so admins can see failures without grepping logs.

### `MailerAudienceSyncJob` (Hangfire recurring)

```csharp
public sealed class MailerAudienceSyncJob(IMailerAudienceSyncService sync, ILogger<MailerAudienceSyncJob> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var results = await sync.SyncAllAsync(ct);
        log.LogInformation("Audience sync ran for {Count} audiences", results.Count);
    }
}
```

Registered in `HangfireConfiguration` with cron from `MailerLiteOptions.AudienceSyncCron` (new option, default `0 6 * * *` Madrid time — early morning, low ML traffic, fresh for the day's admin checks). Job runs sequentially across audiences (no parallelism — keeps ML rate-limit headroom).

## 4. UI

### `/Mailer/Admin` index — Audiences card (new)

Rendered as a partial below the existing dashboard summary, MailerLite groups list, and drift report.

```
┌─ Audiences ───────────────────────────────────────────┐
│ Humans - Ticket no Shifts                             │
│   533 humans match  ·  17 ML-unsubscribed             │
│   516 currently in group  ·  last sync 2h ago         │
│                                  [ Push Now ]         │
└───────────────────────────────────────────────────────┘
```

Per-row stats come from `IMailerAudienceSyncService.ComputeStatsAsync` — a single ML subscriber sweep is shared across all audiences on the page render (compute stats for the first audience, reuse the in-memory subscriber map for the rest). At one audience today, fan-out is irrelevant; at five, sharing the sweep saves four full enumerations.

### Routes

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Mailer/Admin` | GET | `AdminOnly` | Dashboard, extended with Audiences card |
| `/Mailer/Admin/Audiences/{key}/Sync` | POST | `AdminOnly` | Trigger on-demand sync for one audience |

The controller action looks up the `IMailerAudience` by `Key` from the injected `IEnumerable<IMailerAudience>`. 404 if not found.

### Antiforgery + TempData

`POST /Mailer/Admin/Audiences/{key}/Sync` requires antiforgery token (matches existing `/Mailer/Admin/Import/Commit` pattern). On success, sets `TempData["Banner"]` with the summary line (e.g. `"Synced: 0 created, 8 newly assigned, 3 unassigned, 0 errors."`) and redirects back to the index.

## 5. Configuration

`MailerLiteOptions` (existing class) gains:

```csharp
public sealed class MailerLiteOptions
{
    public string BaseUrl { get; init; } = "https://connect.mailerlite.com";
    public string ApiVersion { get; init; } = "2038-01-01";
    public string ApiKey { get; init; } = string.Empty;
    public string AudienceSyncCron { get; init; } = "0 6 * * *";   // new
    public int BulkImportChunkSize { get; init; } = 50;            // new — per ML docs ceiling
}
```

`MAILERLITE_API_KEY` continues to be set via environment variable / Coolify secret; cron and chunk size are config-only (bound in `appsettings.json`).

## 6. Section invariants — updates to `docs/sections/Mailer.md`

Replace:

> - `IMailerLiteService` exposes only GET-shaped methods. No `Create`/`Update`/`Delete`/`Upsert`/`Add`/`Remove`/`Set`/`Post`/`Put`/`Patch` prefixes. Pinned by `MailerArchitectureTests.IMailerLiteService_HasNoWriteMethods`.
> - `MailerLiteClient` runtime-rejects any non-GET HTTP method with `InvalidOperationException`.

with:

> - `IMailerLiteService` exposes reads + four narrow outbound writes: `CreateGroupAsync`, `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`. The set of allowed write methods is pinned by `MailerArchitectureTests.IMailerLiteService_OnlyAllowsAudienceWrites`.
> - Every outbound write targets an ML group whose `Name` starts with `"Humans - "`. `MailerLiteClient` runtime-rejects writes against non-`"Humans - "` groups with `InvalidOperationException`. Pinned by `MailerLiteClientWriteGuardTests.RejectsWritesToNonHumansGroups`.
> - All `IMailerAudience` implementations target group names starting with `"Humans - "`. Pinned by `MailerArchitectureTests.AllAudiences_UseHumansPrefix`.

Remove the line:

> - Inbound-only is a known compliance gap. **Outbound is the next slice and must ship before any other Mailer feature.** …

(Replaced by the section's new outbound capabilities documented above.)

Add to *Cross-Section Dependencies*:

> - **Tickets:** `ITicketQueryService.GetUserIdsWithMatchedAttendeesAsync` — audience-side ticket-holder enumeration for `TicketNoShiftsAudience`.
> - **Shifts:** `IShiftSignupService.GetActiveCommittedUserIdsForEventAsync` and `IShiftManagementService.GetActiveAsync` — audience-side shift commitment + active-event lookup.

Add to *Routing*:

> - `/Mailer/Admin/Audiences/{key}/Sync` — on-demand audience push (POST)

## 7. Architecture invariants — tests

`tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs` updates:

- **Replace** `IMailerLiteService_HasNoWriteMethods` with `IMailerLiteService_OnlyAllowsAudienceWrites`:
  - Reflection scan of `IMailerLiteService` methods; allowed writes are exactly `CreateGroupAsync`, `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`. Any other write-shaped name (`Update*`, `Delete*` not on the allow-list, `Set*`, `Patch*`, additional `Post*`, etc.) fails the test.
- **New** `MailerAudienceSyncService_LivesInApplication_NoEF` — same shape as the existing `MailerImportService` arch pin.
- **New** `AllAudiences_UseHumansPrefix` — load all `IMailerAudience` registrations from DI, assert every `MailerLiteGroupName.StartsWith("Humans - ")`.
- **New** `AllAudiences_HaveUniqueGroupNamesAndKeys` — assert `MailerLiteGroupName` and `Key` are unique across registrations (two audiences would race over the same group otherwise).

`tests/Humans.Application.Tests/Services/Mailer/MailerLiteClientWriteGuardTests.cs` extends:

- **New** `RejectsWritesToNonHumansGroups` — with a `FakeHttpMessageHandler` that returns a group whose name is `"Newsletter"`, calling `AssignSubscriberToGroupAsync(..., that.Id, ...)` throws `InvalidOperationException` before any HTTP write is dispatched.
- **New** `CreateGroupAsync_RejectsNonHumansName` — `CreateGroupAsync("Newsletter")` throws synchronously.

## 8. Service + audience tests

`tests/Humans.Application.Tests/Services/Mailer/MailerAudienceSyncServiceTests.cs` (new):

- **Idempotency** — second run after no Humans/ML state change → all deltas zero, one summary audit entry with zero counts.
- **ML-unsubscribed user filtered** — candidate whose ML subscriber `status = unsubscribed` is bucketed `ExcludedUnsubscribed`, never assigned. Same for `bounced` and `junk`.
- **New user (no ML subscriber record)** — bucketed `ToCreateAndAssign`, uses `BulkImportSubscribersToGroupAsync`.
- **Removal** — user who was in the group but no longer qualifies → `ToUnassign`, single-call unassign issued.
- **Group auto-create** — first run with missing group → calls `CreateGroupAsync`, then assigns.
- **Group prefix violation** — an `IMailerAudience` impl whose `MailerLiteGroupName` lacks `"Humans - "` causes `SyncAsync` to throw before any ML call. (Belt-and-braces alongside the `AllAudiences_UseHumansPrefix` arch test.)
- **Error counting** — an assign call that throws is counted in `errors`; sync continues; summary audit records the count.
- **Audit metadata shape** — verify keys and value types on the summary entry's metadata bag.

`tests/Humans.Application.Tests/Services/Mailer/TicketNoShiftsAudienceTests.cs` (new):

| Scenario | In audience? |
|----------|:-:|
| Has matched ticket (Valid), no shift signup | ✅ |
| Has matched ticket (CheckedIn), no shift signup | ✅ |
| Has matched ticket but a Pending shift signup | ❌ |
| Has matched ticket but a Confirmed shift signup | ❌ |
| Has matched ticket and a Refused signup only | ✅ |
| Has matched ticket and a Bailed signup only | ✅ |
| Has matched ticket and a Cancelled signup only | ✅ |
| Has matched ticket and a NoShow signup only | ✅ |
| Buyer-only match (TicketOrder matched, no Valid attendee) | ❌ |
| No ticket at all | ❌ |
| Shift signup is on a non-active event | ✅ (only active event counts) |
| No active event exists | empty set |

`tests/Humans.Web.Tests/Controllers/Mailer/MailerAdminControllerAudienceSyncTests.cs` (new):

- `POST /Mailer/Admin/Audiences/ticket-no-shifts/Sync` — admin → 302 with TempData banner.
- Non-admin → 403.
- Unknown `key` → 404.
- Antiforgery token required.

## 9. Audit log

New action: `AuditAction.MailerLiteAudienceSyncCompleted`.

Audit metadata schema:
```json
{
  "audience_key": "ticket-no-shifts",
  "group_id": "12345",
  "group_name": "Humans - Ticket no Shifts",
  "candidates": 533,
  "excluded_unsubscribed": 17,
  "created": 0,
  "assigned": 8,
  "already_assigned": 508,
  "unassigned": 3,
  "errors": 0
}
```

No PII in the audit metadata — counts only. Matches the existing `MailerLiteReconciliationCompleted` pattern.

Update `docs/sections/AuditLog.md` to list the new action under Mailer's writes.

## 10. Build sequence

The slice is one PR. Logical commit groups for review:

1. **Outbound primitives** — `IMailerLiteService` write methods, `MailerLiteClient` impls with prefix guard, write-guard tests, arch tests updated. No audience code yet — proves the outbound slice is self-consistent and prefix-safe.
2. **Audience framework** — `IMailerAudience`, `IMailerAudienceSyncService` + impl, DI registration shape, sync service tests with a fake audience.
3. **First audience** — `TicketNoShiftsAudience` + new read methods on `ITicketQueryService` / `IShiftSignupService` it depends on. Audience tests.
4. **UI + job** — dashboard card, `POST /Mailer/Admin/Audiences/{key}/Sync`, Hangfire job + cron config, controller tests.
5. **Section doc + AuditLog doc updates** — `docs/sections/Mailer.md` invariants, `docs/sections/AuditLog.md` new action.

## 11. Open questions for implementer

1. **ML v2 endpoint shapes** — confirm `POST /api/groups/{group_id}/subscribers/import` exists and document its max batch size in `MailerLiteOptions.BulkImportChunkSize`'s comment. If the endpoint has been retired in current ML v2, fall back to a per-subscriber loop in `BulkImportSubscribersToGroupAsync` and reduce `BulkImportChunkSize` to 1 (or remove the option).
2. **Existing tickets read** — confirm whether `ITicketQueryService.GetAllMatchedUserIdsAsync` (referenced in `Tickets.md`) returns exactly the set we want (Valid|CheckedIn attendee match, buyer-only excluded), or whether `TicketNoShiftsAudience` needs a new narrowed read method `GetUserIdsWithMatchedAttendeesAsync`. Prefer reusing the existing method; add a new one only if its semantics don't match.
3. **Primary email lookup** — confirm `IUserEmailService` exposes a bulk `GetPrimaryEmailsByUserIdsAsync` shape; if not, add one as part of this PR (Profiles section), or fall back to per-user `GetPrimaryEmailAsync` calls (acceptable at 500-user scale).
