<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Governance.Contracts/**
  src/Sections/Humans.Consent/Services/ConsentService.cs
  src/Sections/Humans.Governance.Contracts/MembershipStatusLabels.cs
  src/Humans.Base/Constants/SystemTeamIds.cs
  src/Sections/Humans.Teams/Services/SystemTeamSyncJob.cs
  src/Sections/Humans.Users/Jobs/SuspendNonCompliantMembersJob.cs
-->
<!-- freshness:flag-on-change
  The 6-bucket partition logic, priority order, or consent check rule may have changed; re-verify the table and state diagram.
-->

# Membership Status Partition

## Overview

Every human in the system falls into exactly one of 6 mutually exclusive status categories. These categories are computed by `IMembershipCalculator.PartitionUsersAsync()`. [Shared Logic](#shared-logic) lists who consumes it and who computes their own buckets instead.

These are reporting buckets for consent-review and language statistics, not admission or access states. “Pending Approval” means the review annotation is outstanding; it does not block Volunteer admission. `ComputeStatusAsync` and `GetMembershipSnapshotAsync` instead classify from stored account state, active affiliations and elapsed consent deadlines, independently of review.

## The 6 Buckets

| Status | Criteria | Badge Color |
|--------|----------|-------------|
| **Active** | Approved, not suspended, all required consents signed | Green |
| **Pending Approval** | Profile exists, not yet approved by Consent Coordinator | Yellow |
| **Missing Consents** | Approved but missing one or more required legal consents | Blue/Info |
| **Incomplete Signup** | Signed in via Google but no Profile created — or the signup was rejected (Consent's review `IsRejected`) | Gray |
| **Suspended** | Manually suspended by admin or auto-suspended for expired consents | Red |
| **Pending Deletion** | Requested account deletion (30-day window) | Dark |

**Invariant:** All 6 bucket counts sum to total humans. No human appears in more than one bucket.

**Priority order:** PendingDeletion > IncompleteSignup (no profile) > Suspended > PendingApproval > MissingConsents/Active. Rejected signups (Consent's review `RejectedAt` set) fall out at the PendingApproval step and land in IncompleteSignup.

## Reporting Bucket Transitions

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

- **`PreferredLanguageCardViewComponent`** (`Humans.Users`) — the per-status language breakdown

`SystemTeamSyncJob` does not consume this partition. `SyncVolunteersTeamAsync` computes Volunteers eligibility directly (`HasRequiredNameFields && !IsSuspended && !review.IsRejected`, then `GetUsersWithAllRequiredConsentsForTeamAsync`).

The **Admin /Humans list** also does **not** use this partition. It derives its own status buckets directly from `UserInfo` flat predicates (`AdminHumanListAssembler`) — no consent lookup, so there is no Active/Missing-Consents split. It adds the tombstone buckets **Merged** (`MergedAt`) and **Deleted** (`IsTombstone`) plus a cross-cutting **Has Name** filter (`HasRequiredNameFields`) — the meaningful "active" signal now that every account carries a profile.

## Consent Check

"Active" requires all required consents for the Volunteers team to be signed. This is checked via `GetUsersWithAllRequiredConsentsForTeamAsync(userIds, SystemTeamIds.Volunteers)` which compares signed `ConsentRecord` entries against current `DocumentVersion` requirements.
