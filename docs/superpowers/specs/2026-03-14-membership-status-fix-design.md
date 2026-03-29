# Membership Status Fix Design

**Date:** 2026-03-14
**Status:** Draft
**Issue:** #115

## Problem

"Active" means different things in different places:
- **Dashboard** counts "Active" as `!IsSuspended` (includes unapproved humans and those with lapsed consents)
- **Admin /Humans page** counts "Active" as `IsApproved && !IsSuspended` (ignores consent state)
- **Volunteers team sync** requires `IsApproved && !IsSuspended && all required consents`

Dashboard counts overlap and don't sum to total. Consent state is invisible — an approved human missing consents appears "Active" on the dashboard but isn't in the Volunteers team, with no explanation.

## Solution

Define a single **mutually exclusive partition** of all humans, computed by shared logic. Dashboard, admin filters, and team sync all use the same partition.

### The Partition (6 buckets)

Every human falls into exactly one:

| Status | Criteria | Priority |
|--------|----------|----------|
| **Pending Deletion** | `DeletionRequestedAt != null` | Checked first — overrides all other states |
| **Suspended** | `Profile.IsSuspended == true` | Checked second |
| **Incomplete Signup** | `Profile == null` | No profile created yet |
| **Pending Approval** | `!Profile.IsApproved` | Consent Coordinator hasn't cleared |
| **Missing Consents** | `Profile.IsApproved && missing required consents` | Approved but lapsed/unsigned docs |
| **Active** | `Profile.IsApproved && all required consents signed` | Full access, in Volunteers team |

**Priority order matters** — a suspended user who also requested deletion is "Pending Deletion", not "Suspended". Evaluation is top-down.

**Invariant:** `IncompleteSignup + PendingApproval + Active + MissingConsents + Suspended + PendingDeletion = Total`

### Shared Logic: `IMembershipCalculator.PartitionUsersAsync`

New method on the existing `IMembershipCalculator` interface:

```csharp
Task<MembershipPartition> PartitionUsersAsync(
    IEnumerable<Guid> userIds, CancellationToken ct = default);
```

Returns:

```csharp
public record MembershipPartition(
    HashSet<Guid> IncompleteSignup,
    HashSet<Guid> PendingApproval,
    HashSet<Guid> Active,
    HashSet<Guid> MissingConsents,
    HashSet<Guid> Suspended,
    HashSet<Guid> PendingDeletion);
```

**Implementation** (in `MembershipCalculator`):

1. Load all profiles for the given user IDs (single query)
2. Load deletion-requested flags (from User entity)
3. For users with `IsApproved && !IsSuspended`, call existing `GetUsersWithAllRequiredConsentsForTeamAsync(userIds, SystemTeamIds.Volunteers)` to split Active vs MissingConsents
4. Assign each user to exactly one bucket using the priority order above

This reuses the existing consent check logic — no duplication.

### Consumer Changes

**Dashboard (`GetAdminDashboardAsync`):**

Replace the current ad-hoc count queries with:
```csharp
var allUserIds = await _dbContext.Users.Select(u => u.Id).ToListAsync(ct);
var partition = await _membershipCalculator.PartitionUsersAsync(allUserIds, ct);
```

Return partition counts. Remove the separate `pendingConsents` calculation — it's now `partition.MissingConsents.Count`.

Keep the tier application stats (pending applications, Colaborador/Asociado counts) as separate counts — these are orthogonal to the membership partition.

**Admin /Humans filters:**

Replace current filter options with:
- All Statuses
- Active
- Pending Approval
- Missing Consents *(new)*
- Incomplete Signup *(renamed from "Inactive")*
- Suspended
- Pending Deletion *(renamed from "Deleting")*

The filter implementation calls `PartitionUsersAsync` and filters the user list to the matching bucket. At ~500 users, loading all IDs and partitioning in memory is efficient.

**SystemTeamSyncJob (`SyncVolunteersTeamAsync`):**

Replace the current inline consent check with:
```csharp
var allApprovedIds = await _dbContext.Profiles
    .Where(p => p.IsApproved && !p.IsSuspended)
    .Select(p => p.UserId)
    .ToListAsync(ct);
var partition = await _membershipCalculator.PartitionUsersAsync(allApprovedIds, ct);
var eligibleUserIds = partition.Active.ToList();
await SyncTeamMembershipAsync(team, eligibleUserIds, ct);
```

Same result, shared logic.

**Dashboard view:**

Update the Board dashboard view to show the 6 partition counts instead of the current confusing mix. Each count links to the corresponding admin filter.

**Admin /Humans view:**

Update status filter buttons to match the 6 categories. Update status badges on each human row to use the partition status.

### What Doesn't Change

- `Profile.IsApproved`, `Profile.IsSuspended`, `User.DeletionRequestedAt` — no schema changes
- `MembershipStatus` enum — still exists, `ComputeMembershipStatus()` still works for per-user display
- Consent model (LegalDocument, DocumentVersion, ConsentRecord) — untouched
- Tier applications — separate from the partition, shown as additional dashboard stats
- `SuspendNonCompliantMembersJob` — still sets `IsSuspended` based on grace period
- `SendReConsentReminderJob` — still sends reminders

### State Diagram

```
                    ┌──────────────────┐
                    │ Incomplete Signup │
                    │  (no profile)     │
                    └────────┬─────────┘
                             │ completes profile
                             ▼
                    ┌──────────────────┐
                    │ Pending Approval  │
                    │  (!IsApproved)    │
                    └────────┬─────────┘
                             │ Consent Coordinator clears
                             ▼
              ┌──────────────────────────────┐
              │            Active             │
              │ (approved + all consents)     │◄──── re-signs consents
              └──────┬───────────┬───────────┘
                     │           │
         consent     │           │ admin suspends
         lapses      │           │
                     ▼           ▼
         ┌─────────────────┐  ┌───────────┐
         │ Missing Consents│  │ Suspended │
         │ (grace period)  │  │           │
         └────────┬────────┘  └─────┬─────┘
                  │                  │
       grace      │                  │ admin unsuspends
       expires    │                  │
                  ▼                  │
           ┌───────────┐            │
           │ Suspended │◄───────────┘
           │ (auto)    │
           └───────────┘

    Any state ──→ Pending Deletion (user requests deletion)
    Pending Deletion ──→ (deleted after 30 days)
```

## Testing

- **Unit test:** `PartitionUsersAsync` returns correct partition for a mix of user states
- **Unit test:** All 6 buckets sum to total input count
- **Unit test:** A user in exactly one bucket (not double-counted)
- **Integration:** Dashboard counts match admin page filtered counts
