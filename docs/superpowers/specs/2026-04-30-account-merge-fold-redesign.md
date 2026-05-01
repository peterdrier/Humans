# Spec: AccountMergeService fold-into-target redesign

**Date:** 2026-04-30
**Status:** Draft (revised after PR peterdrier#378 review)
**Branch:** `spec/account-merge-fold-redesign`

## Goal

Change `AccountMergeService.AcceptAsync` from an **anonymize-source** model (wipe source's data, anonymize source User) to a **fold-into-target** model (re-FK source's data to target where it makes sense, leave source as a tombstone, and follow the `MergedToUserId` chain on read for the rows that must stay put).

## Background

Today, accepting a merge does this:

1. Source's roles are revoked (`RevokeAllActiveAsync(sourceUserId)`)
2. Source's external logins are deleted (`RemoveExternalLoginsAsync(sourceUserId)`)
3. Source's emails are deleted (`RemoveAllForUserAndSaveAsync(sourceUserId)`)
4. Source's User+Profile are anonymized
5. Pending email-verification flag transfers to target
6. Source's non-system team memberships ARE moved to target (current code already does this via `ITeamService`)

Source becomes a tombstone with no trail back to its data; target gains nothing except the verification flag and team memberships.

This is wrong for every cross-section table other than teams. The merge represents "this is one human, not two" — every piece of source data was something the human did, and the human is now target. Tickets purchased by source must work for the merged identity. Roles assigned to source (Board membership, Coordinator status) belong to the unified human. Today's behavior throws all of that away.

For append-only / immutable rows (audit log, consent records, budget audit log), we **don't rewrite history** — we leave them at the source tombstone and teach reads for target to follow the `MergedToUserId` chain.

## Design

### Core decision

**Source data re-FKs to target everywhere it can. Append-only/immutable rows stay at the source tombstone, and reads for target follow the `MergedToUserId` chain.** Source User row stays as a tombstone (`MergedToUserId = target.Id`, `MergedAt = now`, anonymized profile). Tombstone exists so admin views can show "user A → merged into B on date X" rather than "user A never existed", AND so the immutable-row chain-follow has a stable identity to point at. Merges are one-way (no unmerge).

### Conflict / merge rules

| Source data | Re-FK? | Owning section interface | Conflict rule (same key on both source and target) |
|---|---|---|---|
| `UserEmail` | yes | `IUserEmailService` | OR-combine `IsVerified`; **target's `IsPrimary` and `IsGoogle` win** (target chose those settings); same address → collapse, keep target row |
| `AspNetUserLogins` | yes | `IUserService` | Same `Provider+ProviderKey` on both = literally the same OAuth identity duplicated → keep target's, drop source's |
| `Profile` row | merge | `IProfileService` | Source row anonymized + tombstoned, NOT deleted (keeps `profiles.user_id` FK valid for any historical reference; tombstone-style) |
| `ContactField` (Profile sub-aggregate) | yes | `IContactFieldService` | Move; if target already has the same `Type`+`Value` then drop source's (dedup) |
| `VolunteerHistory` entries (CV) | yes | `IProfileService` | Move; entries are `(year, role)` keyed — if source and target have an identical entry, keep one |
| `Languages` (Profile sub-aggregate) | yes | `IProfileService` | Move with **dedup**: if both rows say "English", keep one row (highest proficiency wins on tie) |
| `CommunicationPreference` | yes | `ICommunicationPreferenceService` | Same key on both → keep most-recent `UpdatedAt` |
| `EventParticipation` | yes | `IUserService` (Users section owns `event_participations` per `docs/sections/Users.md`) | Same event on both → keep highest-status row (`Attended > Ticketed > NoShow > NotAttending`, per `ParticipationStatus` enum) |
| `Tickets` | yes | `ITicketSyncService` (write surface; `ITicketQueryService` is read-only by convention) | No same-key conflicts (every ticket is unique per purchase) |
| `RoleAssignment` | yes | `IRoleAssignmentService` | Same active role on both → keep target's, drop source's |
| `TeamMember` | yes | `ITeamService` | Already implemented in current `AcceptAsync` — add target to source's non-system teams, remove source. System teams (Volunteers etc.) are managed automatically. |
| `TeamJoinRequest` | yes | `ITeamService` | Move; if target already has an active request to the same team, drop source's |
| `ShiftSignup` | yes | `IShiftSignupService` | Plain re-FK; shift signups are unique per slot |
| `VolunteerEventProfile`, `VolunteerTagPreference` | yes | `IShiftManagementService` (owns these tables per `docs/sections/Shifts.md`) | Move; target's row wins on `(eventYear, userId)` collision |
| `GeneralAvailability` | yes | `IGeneralAvailabilityService` (owns `general_availability` per `docs/sections/Shifts.md`) | Move; target's row wins on `(eventYear, userId)` collision |
| `Notification` recipients (`notification_recipients`) | yes | `INotificationService` | Plain re-FK on `UserId`; the parent `Notification` row is shared (resolution is shared across recipients), so only the per-user delivery row moves. If target already has a delivery row for the same notification, drop source's. |
| `CampaignGrant` | yes | `ICampaignService` | Move; per-campaign uniqueness — if target already has a grant on that campaign, drop source's |
| `CampLead`, `CampRoleAssignment` | yes | `ICampService` | Move; same `(campId, userId, role)` on both → keep target's |
| `Application` (Governance) | yes | `IApplicationDecisionService` | Move all historical applications; no conflict rule (applications are unique events) |
| `FeedbackReport` + `FeedbackMessage` | yes | `IFeedbackService` | Move (authorship transfers to merged human so they can see their own report history) |

**Stays at source tombstone — read-side chain-follow:**

| Data | Section | Reason |
|---|---|---|
| `AuditLogEntry` | `IAuditLogService` | Append-only (`design-rules.md §12`). Reads of "audit log for user X" must follow `MergedToUserId` chain to also include rows attributed to merged-in tombstones. |
| `ConsentRecord` | `IConsentService` | DB triggers block UPDATE/DELETE. Same chain-follow rule on read. |
| `BudgetAuditLog` | `IBudgetService` | Append-only. Same chain-follow rule on read. |

**Read-side chain-follow** is the only behaviour change required outside merge for the immutable-row sections: anywhere we currently filter by `userId`, we must instead filter by `userId OR <set of tombstone ids whose MergedToUserId == userId>`.

**Shared lookup primitive:** `IUserService.GetMergedSourceIdsAsync(Guid targetUserId, CancellationToken ct)` returns the set of source tombstone Ids whose `MergedToUserId == targetUserId`. This is the single canonical entry point for chain-follow lookups; AuditLog, Consent, and BudgetAuditLog reads all call it (rather than each section reinventing its own query). The set is small (typically zero, usually one) and is cheap to fetch via `IUserRepository`. Cache for the duration of the request if a section makes multiple chain-follow calls.

### Tombstone

Source User row keeps:
- `Id` (unchanged — append-only chains point at this)
- `MergedToUserId` (new column, nullable Guid FK to `AspNetUsers`)
- `MergedAt` (new column, nullable Instant)

Source User row is anonymized (matches the existing `IUserRepository.AnonymizeForMergeAsync` and `IProfileRepository.AnonymizeForMergeByUserIdAsync` behaviour):
- `Email` cleared (handled by Identity since the column still exists)
- `UserName` randomized to `merged-<sourceId>@deleted.invalid` — stays unique, not user-visible
- `LockoutEnd` set far-future to ensure source can never sign in
- Profile row is **kept** (so FK references stay valid) but DisplayName/Picture/etc. are cleared

Source User must have **no live cross-section data** after the merge other than the immutable-row entries the chain-follow rule covers (`AuditLog`, `ConsentRecord`, `BudgetAuditLog`). Everything else has re-FK'd to target.

### What this spec drops from the original §184–190 list

- **`Failed` / `AdminRequired` state.** Cut at this scale (~500 users). The realistic conflict cases — same `Provider+ProviderKey` on both, same email on both — are recoverable by the conflict rules above. Adding a stuck-state for admins to babysit is busywork. If something genuinely looks weird post-merge, an admin reverses by recreating the source User from the tombstone metadata; doesn't need a separate UI flow. (Decision aligns with `audit_log_as_concurrency_safety_net`: at this scale, audit log is the safety net, not pre-merge gates.)

## Data Model Changes

**Schema (additive only — auto-generated migration):**
- `AspNetUsers.MergedToUserId` — `nullable Guid`, FK to `AspNetUsers.Id`, no cascade
- `AspNetUsers.MergedAt` — `nullable timestamptz` (NodaTime `Instant?`)

No drops. No changes to `AccountMergeRequest` (its existing `Pending`/`Accepted`/`Rejected` enum is sufficient — no `Failed` value).

## Implementation

Single PR. Schema additions are purely additive; old anonymize-source code being deleted is code-only (exempt from the no-drops-until-prod-verified rule per `architecture_no_drops_until_prod_verified`).

### Cross-section service methods needed

Each section's **service** (not repository — see `design-rules.md §9`) gains a `ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken)` method that bulk-updates rows + applies the section's conflict rule. The merge service orchestrates by calling each section's service interface.

| Section interface | New method | Notes |
|---|---|---|
| `IUserEmailService` | `ReassignToUserAsync` | OR-combine flags, target wins on `IsPrimary`/`IsGoogle`, collapse same-address |
| `IUserService` | `ReassignLoginsToUserAsync`, `ReassignEventParticipationToUserAsync`, `AnonymizeForMergeAsync`, `GetMergedSourceIdsAsync` | logins drop same-(provider,key) dupes; event_participations highest-status wins; anonymize tombstones source User row; chain-follow lookup primitive |
| `IProfileService` | `ReassignSubAggregatesToUserAsync` | moves VolunteerHistory + Languages; anonymizes source profile row |
| `IContactFieldService` | `ReassignToUserAsync` | dedup on (Type, Value) |
| `ICommunicationPreferenceService` | `ReassignToUserAsync` | most-recent wins per key |
| `ITicketSyncService` | `ReassignToUserAsync` | plain re-FK (write surface — `ITicketQueryService` is read-only by convention) |
| `IRoleAssignmentService` | `ReassignToUserAsync` | already on the interface; drops same-key actives |
| `ITeamService` | `ReassignToUserAsync` | combines current AddMember/RemoveMember dance + TeamJoinRequest moves; replaces the inline foreach in current AcceptAsync |
| `IShiftSignupService` | `ReassignToUserAsync` | re-FK shift_signups (no sub-aggregates — see Shifts split below) |
| `IShiftManagementService` | `ReassignProfilesAndTagPrefsToUserAsync` | moves `volunteer_event_profiles` + `volunteer_tag_preferences` (owned by ShiftManagementService per `docs/sections/Shifts.md`) |
| `IGeneralAvailabilityService` | `ReassignToUserAsync` | moves `general_availability` rows |
| `INotificationService` | `ReassignRecipientsToUserAsync` | re-FK `notification_recipients.UserId`; drop source's row on per-notification collision |
| `ICampaignService` | `ReassignGrantsToUserAsync` | dedup per campaign |
| `ICampService` | `ReassignAssignmentsToUserAsync` | moves `CampLead` + `CampRoleAssignment`; dedup per (camp, role) |
| `IApplicationDecisionService` | `ReassignApplicationsToUserAsync` | plain re-FK (historical applications, no dedup) |
| `IFeedbackService` | `ReassignToUserAsync` | reports + messages |

Each `ReassignToUserAsync` is owned by its section's service and is the **only** entry point into that section's tables for merge purposes. The merge service does not touch tables outside the Profile section directly — it calls section interfaces.

### Sections deliberately NOT in the table

- **`IAuditLogService`** — append-only (`design-rules.md §12`). Reads follow `MergedToUserId` chain.
- **`IConsentService`** — DB triggers block UPDATE/DELETE. Reads follow chain.
- **`IBudgetService` (BudgetAuditLog)** — append-only. Reads follow chain.

For each of these three, the GDPR export and any per-user read path must be updated (separate small PR per section, or all in this one — TBD) to follow the chain. The set of source-tombstones-for-this-target is small and computed once per request.

### Transaction model

The current `AcceptAsync` already uses `TransactionScope` with `TransactionScopeAsyncFlowOption.Enabled`. Each repository creates its own short-lived `DbContext` via `IDbContextFactory`; Npgsql automatically enlists those connections in the ambient scope, so the cross-repository writes either all commit or all roll back. This is the pattern the new fold orchestration follows — it does NOT require sharing a `DbContext` (which would violate §15).

### `AcceptAsync` body shape (illustrative)

```csharp
public async Task<MergeResult> AcceptAsync(Guid mergeRequestId, Guid actorUserId, CancellationToken ct)
{
    // Load merge request, source User, target User. Owner-gate.

    var now = _clock.GetCurrentInstant();

    using (var scope = new TransactionScope(
        TransactionScopeOption.Required,
        new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
        TransactionScopeAsyncFlowOption.Enabled))
    {
        // Profile section (internal — same section as AccountMergeService)
        await _userEmailService.ReassignToUserAsync(sourceId, targetId, now, ct);
        await _profileService.ReassignSubAggregatesToUserAsync(sourceId, targetId, now, ct);
        await _contactFieldService.ReassignToUserAsync(sourceId, targetId, now, ct);
        await _communicationPreferenceService.ReassignToUserAsync(sourceId, targetId, now, ct);

        // Cross-section
        await _userService.ReassignLoginsToUserAsync(sourceId, targetId, now, ct);
        await _userService.ReassignEventParticipationToUserAsync(sourceId, targetId, now, ct);
        await _ticketSyncService.ReassignToUserAsync(sourceId, targetId, now, ct);
        await _roleAssignmentService.ReassignToUserAsync(sourceId, targetId, now, ct);
        await _teamService.ReassignToUserAsync(sourceId, targetId, actorUserId, now, ct);
        await _shiftSignupService.ReassignToUserAsync(sourceId, targetId, now, ct);
        await _shiftManagementService.ReassignProfilesAndTagPrefsToUserAsync(sourceId, targetId, now, ct);
        await _generalAvailabilityService.ReassignToUserAsync(sourceId, targetId, now, ct);
        await _notificationService.ReassignRecipientsToUserAsync(sourceId, targetId, now, ct);
        await _campaignService.ReassignGrantsToUserAsync(sourceId, targetId, now, ct);
        await _campService.ReassignAssignmentsToUserAsync(sourceId, targetId, now, ct);
        await _applicationDecisionService.ReassignApplicationsToUserAsync(sourceId, targetId, now, ct);
        await _feedbackService.ReassignToUserAsync(sourceId, targetId, now, ct);

        // AuditLog / ConsentRecord / BudgetAuditLog: NOT touched. Their reads
        // for target follow the MergedToUserId chain via
        // IUserService.GetMergedSourceIdsAsync.

        // Tombstone source (anonymize User row + set MergedToUserId/MergedAt).
        await _userService.AnonymizeForMergeAsync(sourceId, targetId, now, ct);

        // Mark merge request Accepted + audit + cache invalidations as today.
        scope.Complete();
    }

    return MergeResult.Success;
}
```

### Test coverage

One integration test per re-FK rule, asserting both the happy path AND the conflict rule:

- `AcceptAsync_UserEmails_OrCombinesFlags_KeepsTargetPrimaryAndGoogle`
- `AcceptAsync_UserEmails_CollapsesSameEmail`
- `AcceptAsync_AspNetUserLogins_ReFKs_DropsSameKey`
- `AcceptAsync_Profile_AnonymizesAndKeepsTombstoneRow`
- `AcceptAsync_ContactFields_Move_DedupOnTypeValue`
- `AcceptAsync_VolunteerHistory_Move_DedupIdenticalEntries`
- `AcceptAsync_Languages_Move_DedupKeepHighestProficiency`
- `AcceptAsync_CommunicationPreferences_MostRecentWins`
- `AcceptAsync_EventParticipation_HighestStatusWins_ByEnumPrecedence`
- `AcceptAsync_Tickets_ReFK`
- `AcceptAsync_RoleAssignments_ReFKs_DropsSameKey`
- `AcceptAsync_TeamMembers_AddTargetRemoveSource_NonSystemOnly`
- `AcceptAsync_TeamJoinRequests_Move_DropDuplicateActive`
- `AcceptAsync_ShiftSignups_PlainReFK`
- `AcceptAsync_VolunteerEventProfiles_AndTagPrefs_Move_TargetWinsOnCollision`
- `AcceptAsync_GeneralAvailability_Move_TargetWinsOnCollision`
- `AcceptAsync_NotificationRecipients_Move_DropDuplicate`
- `AcceptAsync_CampaignGrants_Move_DedupPerCampaign`
- `AcceptAsync_CampLeadAndRoleAssignments_Move_DedupPerRole`
- `AcceptAsync_Applications_Move_AllHistorical`
- `AcceptAsync_FeedbackReportsAndMessages_Move`
- `AcceptAsync_AuditLog_NotMutated_StaysAtSourceId`
- `AcceptAsync_ConsentRecords_NotMutated_StaysAtSourceId`
- `AcceptAsync_BudgetAuditLog_NotMutated_StaysAtSourceId`
- `AcceptAsync_TombstonesSourceWithMergedToUserId`
- `AcceptAsync_PreventsSourceLogin`

Plus chain-follow read tests (one per immutable-row section):

- `AuditLog_ReadByUserId_FollowsMergedToUserIdChain`
- `ConsentExport_ForTarget_IncludesSourceTombstoneRecords`
- `BudgetAuditLog_ReadByUserId_FollowsMergedToUserIdChain`

Plus one full-fixture integration test that seeds source with rows on every section, accepts the merge, and asserts source has only immutable-row history + target has all live data + tombstone is set.

### Rollback

Code-only deletion of the anonymize-source path is rollback-safe via `git revert`. The new `MergedToUserId` / `MergedAt` columns stay (nullable, additive — no harm if rolled-back code never reads them).

## Out of scope

- Reversible merges. One-way is fine at this scale.
- Merge initiation / pending-request UX (already exists, no changes).
- Surfacing tombstones in admin UI ("merged to X" badge on User detail page) — could be a tiny followup PR; not required to ship the merge correctness.

## Open questions

1. **Source User's `Email` column.** Identity still uses `User.Email` for some flows (login lookup, password reset). The email-decoupling sequence (PRs 1–4) is moving Identity off the column. Once the source User is tombstoned and `Email` is cleared, anything still reading `User.Email` for the source returns null — verify nothing in current Identity flows breaks. Should be safe since the tombstoned user is locked out.

2. **Chain-follow scope.** The three chain-follow sections (AuditLog, Consent, BudgetAuditLog) currently filter by `userId` directly. They will be updated to call `IUserService.GetMergedSourceIdsAsync` and union the source-tombstone Ids into their per-user filters. Confirm whether the chain-follow update lands in this PR or as a small followup per section. (Recommendation: same PR — the merge isn't truly correct without it.)

3. **Section ownership of `ReassignToUserAsync`.** Each section adds the method on its **service** interface (not repo), per §9. Confirm with section owners before opening the implementation PR.

## Hard rules in effect

- **No DB column drops** (`architecture_no_drops_until_prod_verified`, `architecture_dont_drop_columns_for_decoupling`). Schema additions only.
- **No hand-edited migrations** (`architecture_no_hand_edited_migrations`).
- **No startup guards** (`architecture_no_startup_guards`).
- **No concurrency tokens.**
- **No invented fields** beyond `MergedToUserId` and `MergedAt`. If implementation surfaces a need for more, ASK.
- **Service is the contract** (`feedback_db_enforcement_minimal`). The fold rules are service-enforced, not DB-enforced. No partial unique indexes or check constraints to "prevent" bad merges; the service guarantees them.
- **No cross-section repository injection** (`design-rules.md §9`). Merge orchestration calls section services, never their repos.
- **Append-only entities are not mutated** (`design-rules.md §12`). AuditLog / ConsentRecord / BudgetAuditLog stay at source tombstone; reads follow `MergedToUserId` chain.
