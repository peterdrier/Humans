# Ticket Attendee Contact Import — Design

**Status:** Draft
**Date:** 2026-05-13
**Section:** Tickets
**Issue:** TBD (open after spec approval)

## Goal

When a person buys a ticket whose attendee email doesn't match an existing Humans user, **create a no-profile Humans user** for them (mirroring how the Mailer import provisions users from MailerLite subscribers). This unlocks two downstream wins:

1. Every paying attendee becomes addressable in Humans — they show up under matched-user reports, the dashboard's "Volunteer Ticket Coverage" denominator stays honest, and the existing `EventParticipation(Status = Ticketed | Attended, Source = TicketSync)` derivation automatically marks them as having a valid ticket for the active year.
2. We can later export MailerLite segments like *"People with tickets, but no shifts"* — currently impossible because unmatched attendees have no `UserId` to feed into segment queries.

## Non-Goals

- **Buyer-only matches.** The Tickets section invariant is explicit: *"buyer-only matches do not count — purchasing tickets for others does not give the buyer ticket coverage."* This import only creates users from **attendees**, never from `TicketOrder.BuyerEmail`.
- **A new flag on User/Profile** to record "has a ticket". `EventParticipation` already represents this; no new state.
- **Automatic invocation from inside `TicketSyncService`.** Phase 1 is a manually-triggered admin job. Phase 2 (later, when trust is established) integrates with the sync orchestrator.
- **Inviting/emailing the new user.** Account is created cold; the user can sign in later via the standard sign-in flow against their (now verified) email and complete their profile.

## What Already Exists (and Why This Is Small)

| Primitive | Location | What it does |
|---|---|---|
| `IAccountProvisioningService.FindOrCreateUserByEmailAsync(email, displayName, ContactSource, ct)` | `Humans.Application.Services.Users` | Idempotent: looks up email across all `UserEmail` rows; if no match, creates `User` + verified `UserEmail` (via `AddProvisionedEmailAsync`, `IsVerified = true`) + Stub `Profile`. Audit row `ContactCreated`. |
| `ContactSource.TicketTailor = 2` | `Humans.Domain.Enums.ContactSource` | Already in the enum; currently unused. |
| `IUserEmailService.GetDistinctVerifiedUserIdsAsync(email, ct)` | `Humans.Application.Services.Profile` | Returns distinct user-ids that have a **verified** row for this address (case-insensitive normalized, including gmail/googlemail collapse). |
| `IUserEmailService.FindAnyEmailRowByAddressAsync(email, ct)` | same | Returns `(UserId, EmailId)` for any matching row (verified or unverified) — used to identify the specific unverified row to delete. |
| `IUserEmailService.DeleteEmailAsync(userId, emailId, ct)` | same | Service-level delete that preserves the last *verified* row; unverified rows are deletable without guard. |
| `IUserService.SetParticipationFromTicketSyncAsync(userId, year, status)` | `Humans.Application.Services.Users` | Writes `EventParticipation(Source = TicketSync, Status = Ticketed | Attended)`. |
| `IUserService.GetActiveYearAsync()` via `IShiftManagementService.GetActiveAsync()` | Shifts section | Active event/year used by `EventParticipation` derivation. |
| `ITicketRepository.UpsertAttendeesAsync(IReadOnlyList<TicketAttendee>, ct)` | `Humans.Infrastructure.Repositories.Tickets` | Bulk upsert keyed by `VendorTicketId`. **Existing** write path used by `TicketSyncService`. No new repo method needed. |

The import service is a thin orchestrator that wires these primitives together. No new entities, no new tables, no new migration.

## Decision Matrix

For each unmatched attendee (defined as: `MatchedUserId is null`, `Status in (Valid, CheckedIn)`, `VendorEventId == active event id`), the plan layer assigns one of six decisions:

| Decision | Trigger | Apply behavior |
|---|---|---|
| `AttachVerified` | `GetDistinctVerifiedUserIdsAsync(email).Count == 1` | Set `attendee.MatchedUserId = userId`. No user creation. |
| `AmbiguousMultipleVerified` | `GetDistinctVerifiedUserIdsAsync(email).Count > 1` | Skip. `LogError` (data-integrity violation; same rule as the sync). |
| `DeleteUnverifiedThenCreate` | No verified match; `FindAnyEmailRowByAddressAsync` returns an unverified row | Delete the unverified row via `DeleteEmailAsync`, then `FindOrCreateUserByEmailAsync(email, attendee-name, ContactSource.TicketTailor)`. Creates new user with **verified** `UserEmail` row. Set `attendee.MatchedUserId = newUser.Id`. |
| `CreateNewUser` | No UserEmail row at all matches | `FindOrCreateUserByEmailAsync(email, attendee-name, ContactSource.TicketTailor)`. Creates new user with verified `UserEmail` row. Set `attendee.MatchedUserId = newUser.Id`. |
| `SkipNoEmail` | `AttendeeEmail is null` | — |
| `SkipVoided` | `Status == Void` | — (these are excluded from the input set, but the decision exists for explicit visibility) |

### Squatter protection

The `DeleteUnverifiedThenCreate` decision is the load-bearing protection against email squatting. Without it, a hostile actor could create an account, add `victim@example.com` as an **unverified** `UserEmail` row, wait for the real victim to buy a ticket, and inherit their ticket match. By deleting the unverified row first and provisioning a fresh user, the new account ends up with a **verified** `UserEmail` (`AddProvisionedEmailAsync` sets `IsVerified = true`) owned by the ticket purchaser.

Side effect: if the squatter's only `UserEmail` was the deleted unverified row, the squatter user ends up with zero `UserEmail` rows. `DeleteEmailAsync` only protects against deleting the last *verified* row. This is the existing Mailer import behavior; accepted as-is.

## Display Name

`FindOrCreateUserByEmailAsync` accepts a `displayName` parameter and falls back to the email-prefix if it's null/empty. For ticket-derived users, pass the attendee's full name in priority order:

1. `attendee.LegalName` if non-empty.
2. Otherwise `$"{attendee.FirstName} {attendee.LastName}".Trim()` if either is non-empty.
3. Otherwise null (provisioning service falls back to email-prefix).

This means a fresh user shows up in the admin as e.g. *"Jane Doe"* instead of *"jane.doe1234"*. The user can change it themselves after first sign-in.

## Service Surface

New Application service `IAttendeeContactImportService` in `Humans.Application.Interfaces.Tickets`:

```csharp
public interface IAttendeeContactImportService : IApplicationService
{
    Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default);
    Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        CancellationToken ct = default);
}
```

DTOs in `Humans.Application.Interfaces.Tickets.Dtos`:

```csharp
public sealed record AttendeeImportPlan(
    IReadOnlyList<AttendeeImportDecision> Decisions,
    int TotalUnmatched);

public sealed record AttendeeImportDecision(
    Guid AttendeeId,
    string Email,
    string? AttendeeName,
    string VendorTicketId,
    AttendeeImportOutcome Outcome,
    Guid? TargetUserId,                 // populated for AttachVerified
    Guid? UnverifiedEmailIdToDelete,    // populated for DeleteUnverifiedThenCreate
    Guid? UnverifiedRowUserId,          // owning userId of that unverified row
    IReadOnlyList<Guid>? AmbiguousUserIds); // populated for AmbiguousMultipleVerified

public enum AttendeeImportOutcome
{
    AttachVerified,
    AmbiguousMultipleVerified,
    DeleteUnverifiedThenCreate,
    CreateNewUser,
    SkipNoEmail,
    SkipVoided,
}

public sealed record AttendeeImportResult(
    int TotalAttempted,
    int UsersCreated,
    int AttachedToExistingVerified,
    int UnverifiedRowsDeletedAndUserCreated,
    int AmbiguousSkipped,
    int NoEmailSkipped,
    int VanishedBetweenPlanAndApply,    // attendee no longer unmatched (e.g. fresh sync ran in between)
    int Errors,
    Duration Elapsed);
```

Implementation `AttendeeContactImportService` lives in `Humans.Application.Services.Tickets`. Dependencies:

- `ITicketRepository` (read unmatched attendees, bulk upsert after match)
- `IUserEmailService` (verified/any-row lookups, unverified-row delete)
- `IAccountProvisioningService` (find-or-create user)
- `IUserService` (post-apply `SetParticipationFromTicketSyncAsync`)
- `IShiftManagementService` (active year for participation derivation)
- `IAuditLogService` (single summary audit row at end)
- `IClock`, `ILogger<AttendeeContactImportService>`

No `DbContext`, no `IMemoryCache` — invalidation after the bulk upsert is handled by calling `ITicketQueryService.InvalidateAfterContactImport()` (a new method on the existing service, mirroring the established touch-and-clean pattern: *"controllers, view components, or other domain services do not touch `IMemoryCache` directly"*).

### `BuildPlanAsync` algorithm

1. Resolve active `VendorEventId` via `ITicketRepository.GetSyncStateAsync()`.
2. `var unmatched = await _ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);` — new narrow read on the repo: attendees where `MatchedUserId is null`, `Status in (Valid, CheckedIn)`, `VendorEventId = active`. Returns `IReadOnlyList<TicketAttendee>` (entities, not a projection — we need the full row to upsert later).
3. For each attendee:
   - If `AttendeeEmail` is null → `SkipNoEmail`.
   - Else call `GetDistinctVerifiedUserIdsAsync(email)`:
     - `Count > 1` → `AmbiguousMultipleVerified` (record the user-id list).
     - `Count == 1` → `AttachVerified` (resolve through tombstone follow — see below — and record the live target user id).
     - `Count == 0` → call `FindAnyEmailRowByAddressAsync(email)`:
       - Returns an unverified row → `DeleteUnverifiedThenCreate` (record `UserId` and `EmailId`).
       - Returns null → `CreateNewUser`.

**Tombstone follow** for `AttachVerified`: copy `MailerImportService.ResolveTombstoneAsync` — walk `User.MergedToUserId` to the live target. If a cycle is detected (defensive — shouldn't happen), stop at the current node.

### `ApplyAsync` algorithm

Mirrors `MailerImportService.ApplyAsync` shape (stateless: re-query unmatched attendees at apply time so plan/apply are independent):

```
start = clock.Now
var freshUnmatched = await repo.GetUnmatchedActiveAttendeesAsync(eventId, ct);
var freshById = freshUnmatched.ToDictionary(a => a.Id);

var toUpsert = new List<TicketAttendee>();
var newlyMatchedUserIds = new HashSet<Guid>();

foreach (var decision in plan.Decisions where selectedAttendeeIds.Contains(d.AttendeeId))
{
    if (!freshById.TryGetValue(decision.AttendeeId, out var attendee))
    {
        vanishedBetweenPlanAndApply++;
        continue;
    }
    try
    {
        switch (decision.Outcome)
        {
            case SkipNoEmail:
            case SkipVoided:
            case AmbiguousMultipleVerified:
                break;

            case AttachVerified:
                attendee.MatchedUserId = decision.TargetUserId!.Value;
                toUpsert.Add(attendee);
                newlyMatchedUserIds.Add(attendee.MatchedUserId.Value);
                attachedToExistingVerified++;
                break;

            case DeleteUnverifiedThenCreate:
                if (decision.UnverifiedRowUserId is Guid uid &&
                    decision.UnverifiedEmailIdToDelete is Guid eid)
                    await userEmails.DeleteEmailAsync(uid, eid, ct);
                var (newUser, created) = await provisioning.FindOrCreateUserByEmailAsync(
                    decision.Email, decision.AttendeeName,
                    ContactSource.TicketTailor, ct);
                attendee.MatchedUserId = newUser.Id;
                toUpsert.Add(attendee);
                newlyMatchedUserIds.Add(newUser.Id);
                if (created) usersCreated++;
                unverifiedRowsDeletedAndUserCreated++;
                break;

            case CreateNewUser:
                var (newUser2, created2) = await provisioning.FindOrCreateUserByEmailAsync(
                    decision.Email, decision.AttendeeName,
                    ContactSource.TicketTailor, ct);
                attendee.MatchedUserId = newUser2.Id;
                toUpsert.Add(attendee);
                newlyMatchedUserIds.Add(newUser2.Id);
                if (created2) usersCreated++;
                break;
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        errors++;
        logger.LogError(ex, "Attendee contact import failed for {AttendeeId} ({Email})",
            decision.AttendeeId, decision.Email);
    }
}

if (toUpsert.Count > 0)
{
    await repo.UpsertAttendeesAsync(toUpsert, ct);
}

// EventParticipation derivation — write Ticketed for each newly-matched user
// so coverage reflects immediately without waiting for the next sync.
var activeYear = await shifts.GetActiveAsync(ct);
if (activeYear is not null)
{
    foreach (var userId in newlyMatchedUserIds)
    {
        // Sync only writes Ticketed here; Attended is reserved for the
        // sync's CheckedIn-aware reconciliation pass.
        await users.SetParticipationFromTicketSyncAsync(
            userId, activeYear.Year, ParticipationStatus.Ticketed, ct);
    }
}

ticketQuery.InvalidateAfterContactImport();

await audit.LogAsync(AuditAction.TicketContactsImported,
    entityType: "Tickets", entityId: Guid.Empty,
    description: result.FormatSummary(),
    jobName: nameof(AttendeeContactImportService));
```

**New `AuditAction.TicketContactsImported`** value added to the existing `AuditAction` enum. Description follows Mailer's terse format: `"created=X, attached=Y, unverified-replaced=Z, ambiguous=A, no-email=B, errors=E, elapsed=Tms"`.

### Why `Ticketed`, not `Attended`

Only the sync's reconciliation pass knows which attendees are `CheckedIn`. This import job runs against `Valid` and `CheckedIn` attendees but writes `Ticketed` uniformly because:

- For `Valid` attendees, `Ticketed` is correct.
- For `CheckedIn` attendees, `Ticketed` is a temporary under-statement that the next ticket sync will upgrade to `Attended` via the existing reconciliation logic. Worst case: a few minutes of staleness on the participation status.

This avoids duplicating the sync's "any `CheckedIn` → `Attended`, else any `Valid` → `Ticketed`" rule. Keep one source of truth for that derivation.

## Routing

| Route | Method | Auth Policy | Purpose |
|---|---|---|---|
| `/Tickets/Admin/Contacts` | GET | `TicketAdminOrAdmin` | Preview page — renders plan + per-row checkbox table |
| `/Tickets/Admin/Contacts` | POST | `TicketAdminOrAdmin` | Apply with selected attendee ids |

(Mailer uses `/Mailer/Admin`. Tickets already namespaces admin actions under `/Tickets/Admin/Transfers`, so `/Tickets/Admin/Contacts` fits.)

`TicketAdminOrAdmin` policy already exists. Same role gating as ticket sync.

Discoverability: add a "Import Attendee Contacts" action link from the `/Tickets` dashboard sidebar (same area as the "Sync" / "Full Re-sync" actions).

## Preview UI

GET `/Tickets/Admin/Contacts` renders:

1. **Summary row** at the top: count badges per decision category (`CreateNewUser: 412`, `AttachVerified: 87`, `DeleteUnverifiedThenCreate: 12`, `AmbiguousMultipleVerified: 2 ⚠️`, `SkipNoEmail: 3`).
2. **Decision table** below: one row per attendee with columns
   - Checkbox (default unchecked)
   - Email
   - Attendee name (resolved per Display Name rules)
   - Decision (color-coded)
   - Target user (display name + link), shown for `AttachVerified` / `DeleteUnverifiedThenCreate` to make squatter cases visible
   - VendorTicketId (for cross-reference)
3. **Controls**:
   - `Select all` button (top of table, checks every box)
   - `Select first 1` button — convenience for the test-one-first workflow
   - `Apply Selected` submit button (disabled when 0 rows selected, shows live count)

Sorting: rows ordered by decision (Ambiguous first so they're impossible to miss, then DeleteUnverifiedThenCreate, then CreateNewUser, then AttachVerified, then Skips). Within each group, alphabetic by email.

Paging: same pattern as Mailer's preview (server-side paged at 100/page). Selections must survive paging — store as hidden inputs in the form across pages, or use a sticky session-scoped selection. **Implementation decision deferred to writing-plans:** Mailer's existing approach is the reference; copy whatever it does.

Workflow for "test with one first":

1. Admin loads `/Tickets/Admin/Contacts` → preview renders.
2. Admin clicks `Select first 1` → first row checked.
3. Admin clicks `Apply Selected` → POST runs `ApplyAsync(plan, { selectedId })`.
4. Result page renders summary (`1 user created`) with a link to the new user's admin profile and the now-matched attendee.
5. Admin returns to `/Tickets/Admin/Contacts` → preview re-runs (stateless), the just-processed attendee is no longer in the list.
6. Admin clicks `Select all` → all remaining rows checked → `Apply Selected`.

## Phase 2 (Not in This Spec, Documented for Continuity)

Once Peter trusts the import logic, the service is wired into `TicketSyncService` as a final pass:

```
// Inside RunSyncAsync, after UpsertAttendeesAsync and ComputeVatForOrdersAsync,
// before SyncEventParticipationsAsync:
await _attendeeContactImport.ApplyAsync(
    plan: await _attendeeContactImport.BuildPlanAsync(ct),
    selectedAttendeeIds: <all-decision-attendee-ids>,
    ct);
```

This deletes the admin button (or leaves it as a manual catch-up tool). No service surface change between Phase 1 and Phase 2 — just a new caller. That's the test that the boundaries are right.

## Tests

### Unit tests (`tests/Humans.Application.Tests/Services/Tickets/`)

- `AttendeeContactImportServicePlanTests` — classifier correctness:
  - `Plan_AssignsAttachVerified_WhenSingleVerifiedMatchExists`
  - `Plan_AssignsAttachVerified_FollowsMergedToUserIdTombstone`
  - `Plan_AssignsAmbiguousMultipleVerified_WhenTwoUsersBothVerified`
  - `Plan_AssignsDeleteUnverifiedThenCreate_WhenOnlyUnverifiedRowExists`
  - `Plan_AssignsCreateNewUser_WhenNoUserEmailRowExists`
  - `Plan_AssignsSkipNoEmail_WhenAttendeeEmailIsNull`
  - `Plan_ExcludesVoidedAttendees`
  - `Plan_ExcludesAlreadyMatchedAttendees`
  - `Plan_ExcludesAttendeesFromInactiveEvents`
- `AttendeeContactImportServiceApplyTests` — apply correctness, selection respect, idempotency:
  - `Apply_OnlyProcessesSelectedAttendees`
  - `Apply_AttachVerified_SetsMatchedUserIdAndDoesNotCreate`
  - `Apply_DeleteUnverifiedThenCreate_DeletesRowAndCreatesUserWithVerifiedEmail`
  - `Apply_CreateNewUser_CallsProvisioningWithAttendeeName`
  - `Apply_CreateNewUser_PassesContactSourceTicketTailor`
  - `Apply_WritesEventParticipationTicketedForEachNewlyMatchedUser`
  - `Apply_VanishedAttendees_AreCountedAndSkipped`
  - `Apply_PartialFailures_DontAbortBatch`
  - `Apply_AuditRowWrittenOnce_WithSummary`
- `AttendeeContactImportServiceSquatterTests` — focused on the security property:
  - `Squatter_UnverifiedRowDeleted_BeforeNewUserCreated`
  - `Squatter_NewUserGetsVerifiedRow_NotAttachedToSquatter`

### Architecture tests (`tests/Humans.Application.Tests/Architecture/`)

- Extend `TicketSyncArchitectureTests` (or new `TicketImportArchitectureTests`):
  - `AttendeeContactImportService.Namespace == Humans.Application.Services.Tickets`
  - Implementation does not reference `Microsoft.EntityFrameworkCore`
  - Implementation does not reference `HumansDbContext`
  - Implementation uses `IAccountProvisioningService`, `IUserEmailService`, `IUserService`, `IShiftManagementService`, `ITicketRepository`, `ITicketQueryService`, `IAuditLogService` (sentinel: depends on the cross-section service abstractions, not their implementations)

### Web tests (`tests/Humans.Web.Tests/Controllers/Tickets/`)

- `TicketsAdminControllerContactsTests`:
  - `Get_ReturnsPreviewWithPlanCounts`
  - `Get_RequiresTicketAdminOrAdmin`
  - `Post_AppliesOnlySelectedRows`
  - `Post_EmptySelection_ReturnsValidationError`

## Section Doc Updates

Update `docs/sections/tickets.md`:

- **Concepts:** add a paragraph defining the attendee contact import as a separate manually-triggered job (and note Phase 2 sync integration).
- **Routing table:** add `/Tickets/Admin/Contacts` GET + POST rows.
- **Triggers:** note that on import apply, `MatchedUserId` is set for the selected attendees, `EventParticipation(Ticketed, TicketSync)` rows are written for newly-matched users, ticket caches are invalidated via `ITicketQueryService.InvalidateAfterContactImport`, and audit row `TicketContactsImported` is written.
- **Cross-Section Dependencies:** add `IAccountProvisioningService` (Users section) to the Tickets section's dependency list.
- **Actors & Roles:** extend the `TicketAdmin, Admin` row to mention "import attendee contacts".
- **Negative Access Rules:** add a Board exclusion for the contacts import (same gating as the sync).

Update freshness triggers at the top of `tickets.md` to include `src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs` and its interface.

## Audit + Logging

- `AuditAction.TicketContactsImported` — single summary row at end of apply, mirroring Mailer's `MailerLiteReconciliationCompleted`.
- Per-attendee `_logger.LogError` on failures (no per-attendee audit rows — would flood the audit log).
- `_logger.LogInformation` with summary at end of apply (parity with Mailer).

No per-user `ContactCreated` audit rows in this service — `AccountProvisioningService.FindOrCreateUserByEmailAsync` already writes one per user it creates. No need to duplicate.

## Open Questions Resolved During Brainstorming

| Question | Resolution |
|---|---|
| Display name for new users? | Pass attendee full name (`LegalName` → `FirstName + LastName` → null fallback). |
| Preview screen? | Yes — required. Mirror Mailer's plan/apply split. |
| Match against unverified UserEmails like Mailer does naively? | **No.** Match Mailer's full pattern: verified attaches, unverified deletes-then-creates (squatter protection). |
| New narrow repo method for setting `MatchedUserId`? | **No.** Reuse existing `UpsertAttendeesAsync` — same primitive the sync uses. |
| Buyer email creates a user too? | **No.** Attendees only. Buyer-only matches don't grant ticket coverage per section invariant. |
| Plan/apply preview shows row detail or just counts? | Full row table — needed to catch unintended attach targets. |
| Single-attendee test capability? | Per-row checkboxes + `Select first 1` button. |

## Out-of-Scope (Explicit)

- Reverse direction (push Humans users to the vendor) — not needed; we read from vendor only.
- Automatic re-attempt of failed imports — failures show up in the next preview run and the admin retries manually.
- Per-row audit trail — flooding the audit log isn't worth the granularity. The `ContactCreated` rows written by provisioning + the per-attendee error logs are enough.
- Resolving `AmbiguousMultipleVerified` cases — these are data-integrity errors that require manual cleanup (merge accounts). The import correctly skips them; resolution lives in `/Admin/Users` merge tooling.
- Removing the matched-attendee from MailerLite when the user changes their primary email — out of scope; that's a `MailerLite ↔ Humans` reconciliation concern, not a ticket-import concern.

## Risks

1. **Bulk user creation kicks off downstream invariants.** Every new user triggers Stub Profile creation and a `ContactCreated` audit row. For 1000 new attendees this is 1000 audit rows + 1000 profile rows in a single request. Mitigation: the per-row checkbox workflow is exactly the throttle — admin starts with one, then scales up. If we ever batch all 1000 in one POST and it times out, we add request-streaming or a Hangfire background job, but Phase 1 doesn't pre-optimize for it.
2. **Vanished-attendee race.** If a ticket sync runs between plan-build and apply, an attendee in the plan may already be matched. Handled via re-query at apply time; outcome counted as `VanishedBetweenPlanAndApply`.
3. **Display name from vendor may be empty for some attendees.** Falls back to email-prefix per provisioning service's existing logic. Acceptable.
