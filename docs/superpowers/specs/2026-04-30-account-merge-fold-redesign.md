# Spec: AccountMergeService fold-into-target redesign

**Date:** 2026-04-30
**Status:** Draft
**Branch:** `spec/account-merge-fold-redesign`

## Goal

Change `AccountMergeService.AcceptAsync` from an **anonymize-source** model (wipe source's data, anonymize source User) to a **fold-into-target** model (re-FK source's data to target, leave source as a tombstone).

## Background

Today, accepting a merge does this:

1. Source's roles are revoked (`RevokeAllActiveAsync(sourceUserId)`)
2. Source's external logins are deleted (`RemoveExternalLoginsAsync(sourceUserId)`)
3. Source's emails are deleted (`RemoveAllForUserAndSaveAsync(sourceUserId)`)
4. Source's User+Profile are anonymized
5. Pending email-verification flag transfers to target

Source becomes a tombstone with no trail back to its data; target gains nothing except the verification flag.

This is wrong for every cross-section table. The merge represents "this is one human, not two" — every piece of source data was something the human did, and the human is now target. Tickets purchased by source must work for the merged identity. AuditLog entries authored by source need to attach to the unified actor for GDPR audit. Roles assigned to source (Board membership, Coordinator status) belong to the unified human. Today's behavior throws all of that away.

## Design

### Core decision

**Source data re-FKs to target. Source User row stays as a tombstone (`MergedToUserId = target.Id`, `MergedAt = now`, anonymized profile).** Tombstone exists so admin views can show "user A → merged into B on date X" rather than "user A never existed". Merges are one-way (no unmerge).

### Conflict rules

| Source data | Re-FK | Conflict rule (same key on both source and target) |
|---|---|---|
| `UserEmail` | yes | OR-combine `IsVerified`; **target's `IsPrimary` and `IsGoogle` win** (target chose those settings) |
| `AspNetUserLogins` | yes | Same `Provider+ProviderKey` on both = literally the same OAuth identity duplicated → keep target's, drop source's |
| `EventParticipation` | yes | Same event on both → keep highest-status row (`Going > Maybe > Interested`) |
| `CommunicationPreference` | yes | Same key on both → keep most-recent `UpdatedAt` |
| `Tickets` | yes | No same-key conflicts (every ticket is unique per purchase) |
| `RoleAssignment` | yes | Same active role on both → keep target's, drop source's |
| `AuditLog` | yes | No conflicts (every entry is a unique event) |

For `UserEmail` specifically: if source has the same email address as target (e.g. both have `peter@example.com`), the rows collapse to one. The target's row is kept; the source's row is deleted; the kept row's `IsVerified` is OR-combined.

### Tombstone

Source User row keeps:
- `Id` (unchanged)
- `MergedToUserId` (new column, nullable Guid FK to `AspNetUsers`)
- `MergedAt` (new column, nullable Instant)

Source User row is anonymized:
- `Email` cleared (handled by Identity since it's still on the table for now)
- `UserName` randomized to `merged-<sourceId>@deleted.invalid` or similar — stays unique, not user-visible
- `LockoutEnd` set far-future to ensure source can never sign in
- Profile fields (DisplayName, Picture, etc.) cleared

Source User must have **no other data** after the merge — every cross-section row should have re-FK'd to target. If anything is left dangling, the merge transaction should not commit.

### What this spec drops from the original §184–190 list

- **`Failed` / `AdminRequired` state.** Cut at this scale (~500 users). The only realistic conflict — same `Provider+ProviderKey` on both — is "the same OAuth identity got duplicated across two signups," which is recoverable by just keeping one. Adding a stuck-state for admins to babysit is busywork. If something genuinely looks weird post-merge, an admin reverses by recreating the source User from the tombstone metadata; doesn't need a separate UI flow. (Decision aligns with `audit_log_as_concurrency_safety_net`: at this scale, audit log is the safety net, not pre-merge gates.)

## Data Model Changes

**Schema (additive only — auto-generated migration):**
- `AspNetUsers.MergedToUserId` — `nullable Guid`, FK to `AspNetUsers.Id`, no cascade
- `AspNetUsers.MergedAt` — `nullable timestamptz` (NodaTime `Instant?`)

No drops. No changes to `AccountMergeRequest` (its existing `Pending`/`Accepted`/`Rejected` enum is sufficient — no `Failed` value).

## Implementation

Single PR. Schema additions are purely additive; old anonymize-source code being deleted is code-only (exempt from the no-drops-until-prod-verified rule per `architecture_no_drops_until_prod_verified`).

### Cross-section repository methods needed

Each section's repo gains a `ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken)` method that bulk-updates rows + applies the section's conflict rule. The merge service orchestrates by calling each section's interface.

| Section | New method |
|---|---|
| Profile (UserEmail) | `IUserEmailRepository.ReassignToUserAsync` (collapse same-email, OR-combine flags, keep target primary/Google) |
| Auth (AspNetUserLogins) | Wire via `UserManager` or new `IUserLoginRepository.ReassignToUserAsync` (drop same-key duplicates) |
| Calendar/Events | `IEventParticipationRepository.ReassignToUserAsync` (highest-status wins per event) |
| Notifications | `ICommunicationPreferenceRepository.ReassignToUserAsync` (most-recent wins per key) |
| Tickets | `ITicketRepository.ReassignToUserAsync` (plain re-FK) |
| Auth (RoleAssignment) | `IRoleAssignmentService.ReassignToUserAsync` (drop same-key actives) |
| Audit Log | `IAuditLogRepository.ReassignToUserAsync` (plain re-FK on actor and subject) |

Each method is owned by its section's service/repo per `design-rules.md` (services own their data). The merge service does not touch tables outside Profile directly — it calls section interfaces.

### `AcceptAsync` body shape (illustrative)

```csharp
public async Task<MergeResult> AcceptAsync(Guid mergeRequestId, Guid actorUserId, CancellationToken ct)
{
    // Load merge request, source User, target User. Owner-gate.
    // Open transaction.

    var now = _clock.GetCurrentInstant();

    await _userEmailRepository.ReassignToUserAsync(sourceId, targetId, now, ct);
    await _userLoginRepository.ReassignToUserAsync(sourceId, targetId, now, ct);
    await _eventParticipationRepository.ReassignToUserAsync(sourceId, targetId, now, ct);
    await _communicationPreferenceRepository.ReassignToUserAsync(sourceId, targetId, now, ct);
    await _ticketRepository.ReassignToUserAsync(sourceId, targetId, now, ct);
    await _roleAssignmentService.ReassignToUserAsync(sourceId, targetId, now, ct);
    await _auditLogRepository.ReassignToUserAsync(sourceId, targetId, now, ct);

    // Tombstone source.
    source.MergedToUserId = targetId;
    source.MergedAt = now;
    source.UserName = $"merged-{sourceId}@deleted.invalid";
    source.LockoutEnd = DateTimeOffset.MaxValue;
    // Profile fields cleared.

    // Mark merge request Accepted.
    // Audit log: AuditAction.AccountMergeAccepted (sourceId, targetId, actorUserId).
    // Invalidate FullProfile cache for target.

    // Commit transaction.
    return MergeResult.Success;
}
```

Single transaction across all 7 sections. At 500-user scale on single-server, this is fine — no distributed coordination concerns.

### Test coverage

One integration test per re-FK rule, asserting both the happy path AND the conflict rule:

- `AcceptAsync_UserEmails_OrCombinesFlags_KeepsTargetPrimaryAndGoogle`
- `AcceptAsync_UserEmails_CollapsesSameEmail`
- `AcceptAsync_AspNetUserLogins_ReFKs_DropsSameKey`
- `AcceptAsync_EventParticipation_HighestStatusWins`
- `AcceptAsync_CommunicationPreference_MostRecentWins`
- `AcceptAsync_Tickets_ReFK`
- `AcceptAsync_RoleAssignments_ReFKs_DropsSameKey`
- `AcceptAsync_AuditLog_ReFKsActorAndSubject`
- `AcceptAsync_TombstonesSourceWithMergedToUserId`
- `AcceptAsync_AnonymizesSourceProfile`
- `AcceptAsync_PreventsSourceLogin`

Plus one full-fixture integration test that seeds source with rows on every section, accepts the merge, and asserts source is empty + target has all the data + tombstone is set.

### Rollback

Code-only deletion of the anonymize-source path is rollback-safe via `git revert`. The new `MergedToUserId` / `MergedAt` columns stay (nullable, additive — no harm if rolled-back code never reads them).

## Out of scope

- Reversible merges. One-way is fine at this scale.
- Merge initiation / pending-request UX (already exists, no changes).
- Section-specific edge cases beyond the seven sections listed (if a future section adds user-owned tables, that section adds its own `ReassignToUserAsync`).
- Surfacing tombstones in admin UI ("merged to X" badge on User detail page) — could be a tiny followup PR; not required to ship the merge correctness.

## Open questions

1. **Source User's `Email` column.** Identity still uses `User.Email` for some flows (login lookup, password reset). The email-decoupling sequence (PRs 1–4) is moving Identity off the column. Once the source User is tombstoned and `Email` is cleared, anything still reading `User.Email` for the source returns null — verify nothing in current Identity flows breaks. Should be safe since the tombstoned user is locked out.

2. **AuditLog `ActorUserId` re-FK.** Existing audit rows authored by source get their actor pointer rewritten to target. This is a mass-update of historical rows. GDPR-wise this is fine (it's a unification, not a falsification — both rows are "actions by this human"), but worth a mention in any compliance review.

3. **Section ownership of `ReassignToUserAsync`.** Each section adds the method; the merge service orchestrates. Confirm with section owners (Calendar, Notifications, Tickets, Auth, Audit Log) before opening the implementation PR.

## Hard rules in effect

- **No DB column drops** (`architecture_no_drops_until_prod_verified`, `architecture_dont_drop_columns_for_decoupling`). Schema additions only.
- **No hand-edited migrations** (`architecture_no_hand_edited_migrations`).
- **No startup guards** (`architecture_no_startup_guards`).
- **No concurrency tokens.**
- **No invented fields** beyond `MergedToUserId` and `MergedAt`. If implementation surfaces a need for more, ASK.
- **Service is the contract** (`feedback_db_enforcement_minimal`). The fold rules are service-enforced, not DB-enforced. No partial unique indexes or check constraints to "prevent" bad merges; the service guarantees them.
