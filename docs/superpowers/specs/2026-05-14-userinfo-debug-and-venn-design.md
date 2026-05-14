# UserInfo-driven admin stats, debug table, and ticket Venn

**Date:** 2026-05-14
**Branch:** `feat/userinfo-debug-and-venn`
**Sections:** Users (Admin), Admin (dashboard), Tickets

## Problem

Recent imports (mailing list subscribers, ticket attendees) inflated the user table to ~3k rows. The existing `/Admin` dashboard shows "Active humans 979" sourced from `IProfileService.GetActiveApprovedUserIdsAsync()` (approved-and-not-suspended profiles). The number is technically correct but misleading next to the now-much-larger user count, and the dashboard has no signal of how many of the imported users actually became real members.

The `/Tickets` page has two cards that partition the same population (Volunteer Ticket Coverage + Participation Breakdown donut) and the donut isn't size-bounded.

There is no admin-visible way to spot-check the `UserInfo` cache — the unified read-model that should be the single source of truth for "everything about a person".

## Goals

1. **`/Admin` dashboard:** show Total Users, Active (has profile), Ticket Holders. Drop the existing "Active humans 979" card.
2. **`/Users/Admin/Debug`:** flat paginated/sortable table of all users, every column derived from `UserInfo`, no fallback queries.
3. **`/Tickets`:** replace the redundant coverage card + unbounded donut with a 3-set proportional Venn (Users / Profiles / Tickets) and an UpSet plot of the same sets, side-by-side.
4. **`UserInfo` extension:** carry communication preferences on the cached god-object so the debug page (and future consumers) can see Marketing opt-in without secondary queries.

## Non-goals

- HasShift column on the debug page. `IShiftManager` isn't cached today; pausing until it is. Tracked as a follow-up.
- Shifts set in the Venn / UpSet. Same reason.
- Filtering / search on the debug page. Pure flat table for this iteration.
- Retiring `IProfileService.GetActiveApprovedUserIdsAsync()`. Other call sites likely depend on it; out of scope.

## Architecture

All three deliverables read from a single source: `IUserService.GetAllUserInfos()`, a new method that returns an in-memory snapshot of the `UserInfo` cache held inside `CachingUserService`. No new repository queries are added on the read path. The existing cache invalidation pipeline (the `CachingUserService` write decorator + `UserInfoSaveChangesInterceptor`) is extended to cover the new contributing table (`communication_preferences`).

### Layer changes

| Layer | Change |
|---|---|
| Domain | (none) |
| Application | `UserInfo` record gains `CommunicationPreferences` field; new `CommunicationPreferenceInfo` record. `UserInfo.Create()` accepts the new input. `IUserService` interface gains `GetAllUserInfos()`. |
| Infrastructure | `CachingUserService` exposes the snapshot, injects `ICommunicationPreferenceRepository`, extends `WarmAllAsync` and `RefreshEntryAsync`. `UserInfoSaveChangesInterceptor` adds a `CommunicationPreference` case. |
| Web | New `UsersAdminDebugController`. `AdminController.Index` swaps its stat sources. `TicketController` swaps its dashboard charts. New nav entry in `AdminNavTree`. |

## Detailed design

### 1. `UserInfo` extension — communication preferences

Add to `UserInfo`:

```csharp
IReadOnlyList<CommunicationPreferenceInfo> CommunicationPreferences
```

New projection record (in `src/Humans.Application/UserInfo.cs` alongside the other `*Info` records):

```csharp
public sealed record CommunicationPreferenceInfo(
    Guid Id,
    MessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    Instant UpdatedAt,
    Instant? SubscribedAt);
```

`UserInfo.Create()` gains an `IReadOnlyList<CommunicationPreference> communicationPreferences` parameter. Projection mirrors the existing pattern (order by `Category`, project each row).

Derived accessor on `UserInfo` for the Marketing-specific tri-state:

```csharp
public bool? MarketingOptedOut =>
    CommunicationPreferences
        .Where(c => c.Category == MessageCategory.Marketing)
        .Select(c => (bool?)c.OptedOut)
        .FirstOrDefault();
```

`null` → no row exists for the Marketing category. Tri-state semantics:
- `null` → "—" (e.g., imported user who never hit the prefs flow)
- `true` → "No" (OptedOut)
- `false` → "Yes" (subscribed)

### 2. Cache wiring — `CachingUserService`

Add a snapshot accessor on `IUserService`:

```csharp
IReadOnlyCollection<UserInfo> GetAllUserInfos();
```

Implementation in `CachingUserService`:

```csharp
public IReadOnlyCollection<UserInfo> GetAllUserInfos() => _byUserId.Values.ToArray();
```

`.ToArray()` is a O(N) snapshot — the dict is mutable, so we hand back a stable copy. At 3k users this is allocation-cheap.

Inject `ICommunicationPreferenceRepository`. Both `WarmAllAsync` and `RefreshEntryAsync` are extended:

- `WarmAllAsync`: bulk-load all preferences via a new `ICommunicationPreferenceRepository.GetAllAsync(ct)` (already exists or added if missing), group by `UserId`, look up per-user in the loop.
- `RefreshEntryAsync`: per-user `ICommunicationPreferenceRepository.GetByUserIdReadOnlyAsync(userId, ct)`.

Inner `IUserService.GetUserInfoAsync` (the non-cached path) also needs to load preferences. The caching decorator and the inner service both must pass the same data into `UserInfo.Create()`.

### 3. Invalidation — `UserInfoSaveChangesInterceptor`

Add a case to the entity switch:

```csharp
case CommunicationPreference cp:
    affected.Add(cp.UserId);
    break;
```

No category filter — any preference write triggers a refresh. The whole `CommunicationPreferences` collection on `UserInfo` is being rebuilt anyway.

Update the XML doc on `UserInfoSaveChangesInterceptor` to list `communication_preferences` in the covered-tables list.

### 4. `/Admin` dashboard stats

In `AdminController.Index`, replace the three current calls that feed the dashboard stat row:

| Before | After |
|---|---|
| `_profileService.GetActiveApprovedUserIdsAsync()` → "Active humans" | _removed_ |
| (no equivalent) | `snapshot.Count` → "Total users" |
| (no equivalent) | `snapshot.Count(u => u.Profile != null)` → "Active (has profile)" |
| (no equivalent) | `snapshot.Count(u => u.EventParticipations.Any(HasTicketPredicate))` → "Ticket holders" |

`snapshot` = `_userService.GetAllUserInfos()` taken once at the top of the action.

`HasTicketPredicate` — a single predicate defined alongside `UserInfo` (likely as a `UserInfo` extension method or instance accessor `HasTicket`) so the same definition is used by the dashboard, the debug table, and the tickets page. The predicate matches what the existing `TicketController` uses for `VolunteerCoveragePercent` / `ParticipationHasTicket` — exact form to be confirmed when wiring (the implementation step reads `TicketController` and the participation-projection helpers, then promotes the predicate to a shared `UserInfo.HasTicket` instance accessor).

`AdminDashboardViewModel` gains `TotalUsers`, `ActiveProfileUsers`, `TicketHolders`. `_DashboardStats.cshtml` renders the three cards in place of the removed "Active humans" card. "Shifts staffed" and "Open feedback" cards untouched.

### 5. `/Users/Admin/Debug` page

**Controller:** `src/Humans.Web/Controllers/Users/UsersAdminDebugController.cs`

```csharp
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin/Debug")]
public sealed class UsersAdminDebugController : Controller
{
    private readonly IUserService _userService;

    public UsersAdminDebugController(IUserService userService) => _userService = userService;

    [HttpGet("")]
    public IActionResult Index(int page = 1, int pageSize = 50, string sort = "displayName", string dir = "asc")
    {
        var snapshot = _userService.GetAllUserInfos();
        var rows = snapshot.Select(UserDebugRow.From).ToList();
        var sorted = ApplySort(rows, sort, dir);
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return View(new UsersDebugViewModel(paged, sorted.Count, page, pageSize, sort, dir));
    }
}
```

(Exact location under `Controllers/` follows the existing convention — `Controllers/Users/` if that subdir exists, otherwise `Controllers/`. The implementation step picks the right home based on the repo state at that moment.)

**Row projection:** `UserDebugRow` is a small view-model record built from a `UserInfo`. Each column maps to one expression on the cached object:

| Column | Expression on `UserInfo` |
|---|---|
| UserId | `Id` |
| HasProfile | `Profile != null` |
| HasTicket | `HasTicket` (shared accessor — see §4) |
| Marketing | `MarketingOptedOut` |
| Display name | `DisplayName` |
| Burner name | `Profile?.BurnerName ?? ""` |
| Legal name | `Profile?.FullName ?? ""` |
| HasConsent | `Profile == null ? (bool?)null : Profile.ConsentCheckStatus == ConsentCheckStatus.Cleared` |

**Sorting:** server-side over the in-memory snapshot. `ApplySort` switches on the `sort` query param. Nullable booleans sort `null < false < true` (i.e., "—" first ascending). Strings use `StringComparer.OrdinalIgnoreCase`.

**Paging:** server-side `Skip`/`Take`. `pageSize` clamped to `[10, 200]`. Page count = `ceil(total / pageSize)`.

**View:** `src/Humans.Web/Views/UsersAdminDebug/Index.cshtml`. Match the look of an existing admin table — likely the closest precedent is `Views/AdminMergeRequests/Index.cshtml` or `Views/AdminDuplicateAccounts/Index.cshtml`. Header cells are links that toggle `sort`/`dir`. Pager at the bottom.

**Nav:** add to `AdminNavTree.Groups` under a "Diagnostics" or similar grouping. New entry:

```csharp
new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug", PolicyNames.AdminOnly)
```

### 6. `/Tickets` page — Venn + UpSet

**Remove:**
- "Volunteer Ticket Coverage" card (`Views/Ticket/Index.cshtml` lines ~104–129).
- "Participation Breakdown" donut card (lines ~131–168) and its Chart.js initialization in the page-script block.

**Add:** a single full-width card with two side-by-side panels:
- Left: 3-set proportional Venn — sets are **Users**, **Profiles**, **Tickets**.
- Right: UpSet plot — same three sets.

Both panels in containers with `max-height: 420px; width: 100%` so the layout stops blowing out. Caption beneath: `N total users · M with profile · K with ticket`.

**Data:** computed once in `TicketController` (or whichever action renders `Views/Ticket/Index.cshtml`) from `_userService.GetAllUserInfos()`. Bucket each user into the 4 combinations of `(HasProfile, HasTicket)`:

| HasProfile | HasTicket | Bucket name |
|---|---|---|
| false | false | usersOnly |
| true | false | usersAndProfileOnly |
| false | true | usersAndTicketOnly |
| true | true | all three |

Set sizes derive trivially:
- |Users| = total count
- |Profiles| = usersAndProfileOnly + allThree
- |Tickets| = usersAndTicketOnly + allThree
- |Users ∩ Profiles| = same as |Profiles| (Profiles ⊆ Users)
- |Users ∩ Tickets| = same as |Tickets| (Tickets ⊆ Users)
- |Profiles ∩ Tickets| = allThree
- |all three| = allThree

`TicketDashboardViewModel` gains a `SetMembership` property carrying these counts. The view emits them as `data-*` attributes on the chart containers; the script reads them and feeds the libraries.

**Libraries:**
- `@upsetjs/venn.js` (maintained fork of Ben Frederickson's `venn.js`, MIT, ~12 KB) for the proportional Venn.
- `@upsetjs/bundle` (vanilla wrapper around `@upsetjs/react`, MIT) for the UpSet plot.

Both vendored as static asset references in `_Layout` or the page-script block — match how Chart.js is currently included (`Views/Ticket/Index.cshtml` line ~374). No npm pipeline added; CDN reference with integrity hash, or self-hosted under `wwwroot/lib/`.

**TODO comments** in both the controller and the view: when `IShiftManager` caching lands, extend the bucketing to `(HasProfile, HasTicket, HasShift)`, refresh the diagrams to 4 sets, and update this spec / open follow-up issue link.

## Data flow

```
SaveChanges on contributing tables
  → UserInfoSaveChangesInterceptor.SavingChangesAsync (collects affected userIds)
  → SaveChanges commits
  → SavedChangesAsync → IUserInfoInvalidator.InvalidateAsync(userId)
  → CachingUserService.RefreshEntryAsync(userId) rebuilds the cache entry
       (now also loads communication_preferences)

Reads (dashboard / debug page / tickets page):
  Controller
    → IUserService.GetAllUserInfos()  // snapshot of in-memory dict
    → in-memory LINQ for stats, sort, page, set-membership bucketing
    → view
```

No request-path query against `users`, `profiles`, `event_participations`, `communication_preferences`. If a field is missing from `UserInfo`, the fix is on `UserInfo`, not on the consumer.

## Testing

- **Unit tests** for the new accessors:
  - `UserInfo.MarketingOptedOut` returns null / true / false for the three cases.
  - `UserInfo.HasTicket` matches the predicate used by the existing tickets dashboard (snapshot the existing definition in a test so any drift is caught).
- **Integration test** (existing repository test harness):
  - `CommunicationPreference` write fires `IUserInfoInvalidator.InvalidateAsync` and the next `GetUserInfoAsync` shows the updated value.
- **Controller tests** for `UsersAdminDebugController`:
  - returns admin-policy 403 for non-admin.
  - empty cache → empty rows, total 0, page 1.
  - sorts respect tri-state null-first semantics.
  - paging clamps `pageSize` outside `[10, 200]`.
- **Smoke test** of `/Tickets` after change: page renders, both panels visible, containers stay within the page width.

## Open questions

1. **`HasTicket` predicate definition.** The existing `TicketController` uses some combination of `ParticipationStatus` and possibly `ParticipationSource`. The implementation step reads that code, picks the exact predicate, promotes it to a shared `UserInfo.HasTicket` accessor, and snapshot-tests it. Recording the choice in an inline comment on the accessor.
2. **`AdminNavTree` grouping for the debug page.** A "Diagnostics" group may not exist yet. If not, this work adds it (or slots under an existing close-enough group like "System").

Neither is a blocker — both are implementation-time decisions.

## Out-of-scope follow-ups

- `IShiftManager` caching → enables `HasShift` column on the debug page and Shifts set on the diagrams. Likely a separate spec; this one notes the TODOs.
- Retiring `IProfileService.GetActiveApprovedUserIdsAsync()` from any other dashboards that show "approved" counts. Out of scope until a consumer audit happens.

## Acceptance criteria

- `/Admin` shows three new cards (Total users, Active (has profile), Ticket holders) sourced from the in-memory `UserInfo` snapshot. The "Active humans 979" card is gone.
- `/Users/Admin/Debug` is reachable from the admin nav, lists all users in a paginated/sortable table with the eight specified columns, and produces every cell from `UserInfo` without secondary queries.
- `/Tickets` no longer shows the "Volunteer Ticket Coverage" card or the unbounded donut; it shows a 3-set Venn and an UpSet plot side-by-side, both bounded in height.
- A write to `communication_preferences` for any user invalidates that user's `UserInfo` entry and the next read reflects the change.
- All build, all tests pass.
