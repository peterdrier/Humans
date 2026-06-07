<!-- freshness:triggers
  src/Humans.Application/Services/Governance/MembershipCalculator.cs
  src/Humans.Application/Services/Governance/MembershipQuery.cs
  src/Humans.Application/Services/Consent/ConsentService.cs
  src/Humans.Domain/Constants/MembershipStatusLabels.cs
  src/Humans.Domain/Constants/SystemTeamIds.cs
  src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs
  src/Humans.Infrastructure/Jobs/SuspendNonCompliantMembersJob.cs
-->
<!-- freshness:flag-on-change
  The 6-bucket partition logic, priority order, or consent check rule may have changed; re-verify the table and state diagram.
-->

# Membership Status Partition

## Overview

Every human in the system falls into exactly one of 6 mutually exclusive status categories. These categories are computed by `IMembershipCalculator.PartitionUsersAsync()` and used by the Admin dashboard. (Neither the Admin /Humans list nor the Volunteers team sync uses this partition any longer — the /Humans list derives its own status buckets directly from `UserInfo`, and after the name-only access switch `SystemTeamSyncJob` computes Volunteers eligibility itself from name + consents; see [Shared Logic](#shared-logic).)

## The 6 Buckets

| Status | Criteria | Badge Color |
|--------|----------|-------------|
| **Active** | Approved, not suspended, all required consents signed | Green |
| **Pending Approval** | Profile exists, not yet approved by Consent Coordinator | Yellow |
| **Missing Consents** | Approved but missing one or more required legal consents | Blue/Info |
| **Incomplete Signup** | Signed in via Google but no Profile created | Gray |
| **Suspended** | Manually suspended by admin or auto-suspended for expired consents | Red |
| **Pending Deletion** | Requested account deletion (30-day window) | Dark |

**Invariant:** All 6 bucket counts sum to total humans. No human appears in more than one bucket.

**Priority order:** PendingDeletion > Suspended > IncompleteSignup > PendingApproval > MissingConsents/Active

## State Diagram

```
Incomplete Signup → (completes profile) → Pending Approval
Pending Approval → (Consent Coordinator clears) → Active
Active → (consent lapses) → Missing Consents
Missing Consents → (re-signs) → Active
Missing Consents → (grace period expires) → Suspended
Active → (admin suspends) → Suspended
Suspended → (admin unsuspends) → Active
Any state → (requests deletion) → Pending Deletion
Pending Deletion → (30 days) → Deleted
```

## Shared Logic

`IMembershipCalculator.PartitionUsersAsync(userIds)` is the single source of truth for the consent-aware partition. Consumers:

- **Admin dashboard** (`AdminDashboardService`) — shows count per category

`SystemTeamSyncJob` no longer consumes this partition. After the name-only access switch, `SyncVolunteersTeamAsync` computes Volunteers eligibility directly (`HasRequiredNameFields && !IsSuspended && RejectedAt is null`, then `GetUsersWithAllRequiredConsentsForTeamAsync`), without going through `PartitionUsersAsync`.

The **Admin /Humans list** also does **not** use this partition. It derives its own status buckets directly from `UserInfo` flat predicates (`AdminHumanListAssembler`) — no consent lookup, so there is no Active/Missing-Consents split. It adds the tombstone buckets **Merged** (`MergedAt`) and **Deleted** (`IsTombstone`) plus a cross-cutting **Has Name** filter (`HasRequiredNameFields`) — the meaningful "active" signal now that every account carries a profile.

## Consent Check

"Active" requires all required consents for the Volunteers team to be signed. This is checked via `GetUsersWithAllRequiredConsentsForTeamAsync(userIds, SystemTeamIds.Volunteers)` which compares signed `ConsentRecord` entries against current `DocumentVersion` requirements.
