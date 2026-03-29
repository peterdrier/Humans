# Membership Status Partition

## Overview

Every human in the system falls into exactly one of 6 mutually exclusive status categories. These categories are computed by `IMembershipCalculator.PartitionUsersAsync()` and used by the Board dashboard, Admin /Humans filters, and Volunteers team sync.

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

`IMembershipCalculator.PartitionUsersAsync(userIds)` is the single source of truth. Consumers:

- **Board dashboard** — shows count per category
- **Admin /Humans page** — filters by category, shows status badge per human
- **SystemTeamSyncJob** — Volunteers team eligibility = `partition.Active`

## Consent Check

"Active" requires all required consents for the Volunteers team to be signed. This is checked via `GetUsersWithAllRequiredConsentsForTeamAsync(userIds, SystemTeamIds.Volunteers)` which compares signed `ConsentRecord` entries against current `DocumentVersion` requirements.
