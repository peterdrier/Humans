# Membership Status Fix Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix dashboard counts and admin filters to use a single shared partition of all humans into 6 mutually exclusive buckets.

**Architecture:** Add `PartitionUsersAsync` to `IMembershipCalculator` as the single source of truth for membership status. Dashboard, admin filters, and Volunteers team sync all consume it. No schema changes.

**Tech Stack:** .NET 9, EF Core, xUnit + NSubstitute + AwesomeAssertions, NodaTime

**Spec:** `docs/superpowers/specs/2026-03-14-membership-status-fix-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `src/Humans.Application/DTOs/MembershipPartition.cs` | Partition record (6 HashSets) |
| `tests/Humans.Application.Tests/Services/MembershipPartitionTests.cs` | Tests for PartitionUsersAsync |

### Modified Files

| File | Change |
|------|--------|
| `src/Humans.Application/Interfaces/IMembershipCalculator.cs` | Add `PartitionUsersAsync` method |
| `src/Humans.Infrastructure/Services/MembershipCalculator.cs` | Implement `PartitionUsersAsync` |
| `src/Humans.Application/DTOs/AdminDashboardData.cs` | Replace counts with partition-based fields |
| `src/Humans.Infrastructure/Services/OnboardingService.cs` | Rewrite `GetAdminDashboardAsync` using partition |
| `src/Humans.Infrastructure/Services/ProfileService.cs` | Rewrite `GetFilteredHumansAsync` using partition |
| `src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs` | Use `partition.Active` for Volunteers eligibility |
| `src/Humans.Web/Views/Board/Index.cshtml` | Update dashboard cards to show 6 categories |
| `src/Humans.Web/Views/Human/Humans.cshtml` | Update status filter buttons and badges |

---

## Chunk 1: Shared Partition Logic + Consumers

### Task 1: MembershipPartition DTO + Interface

**Files:**
- Create: `src/Humans.Application/DTOs/MembershipPartition.cs`
- Modify: `src/Humans.Application/Interfaces/IMembershipCalculator.cs`

- [ ] **Step 1: Create MembershipPartition record**

```csharp
// src/Humans.Application/DTOs/MembershipPartition.cs
namespace Humans.Application.DTOs;

public record MembershipPartition(
    HashSet<Guid> IncompleteSignup,
    HashSet<Guid> PendingApproval,
    HashSet<Guid> Active,
    HashSet<Guid> MissingConsents,
    HashSet<Guid> Suspended,
    HashSet<Guid> PendingDeletion);
```

- [ ] **Step 2: Add method to IMembershipCalculator**

Add to the interface:

```csharp
/// <summary>
/// Partitions a set of user IDs into 6 mutually exclusive membership categories.
/// Every input user ID appears in exactly one bucket.
/// Priority order: PendingDeletion > Suspended > IncompleteSignup > PendingApproval > MissingConsents/Active.
/// </summary>
Task<MembershipPartition> PartitionUsersAsync(
    IEnumerable<Guid> userIds, CancellationToken ct = default);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeds (method not yet implemented — will fail at runtime but compiles because it's an interface addition)

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/DTOs/MembershipPartition.cs src/Humans.Application/Interfaces/IMembershipCalculator.cs
git commit -m "feat(membership): add MembershipPartition DTO and interface method"
```

### Task 2: Implement PartitionUsersAsync with Tests

**Files:**
- Modify: `src/Humans.Infrastructure/Services/MembershipCalculator.cs`
- Create: `tests/Humans.Application.Tests/Services/MembershipPartitionTests.cs`

- [ ] **Step 1: Write tests**

Test cases — use in-memory DB, FakeClock, seed users with various states:

1. **Active user** — has profile, approved, not suspended, all consents → goes in Active
2. **Pending approval** — has profile, not approved, not suspended → PendingApproval
3. **Suspended** — has profile, is suspended → Suspended
4. **Incomplete signup** — no profile → IncompleteSignup
5. **Pending deletion** — has DeletionRequestedAt set → PendingDeletion (regardless of other state)
6. **Missing consents** — has profile, approved, not suspended, missing a required consent → MissingConsents
7. **All buckets sum to total** — seed mix of users, verify all 6 bucket counts sum to input count
8. **No user in multiple buckets** — verify all 6 sets are disjoint
9. **Priority: deletion overrides suspended** — suspended user with DeletionRequestedAt → PendingDeletion not Suspended

Test pattern: follow `tests/Humans.Application.Tests/Services/ConsentServiceTests.cs` or `CampaignServiceTests.cs` for setup conventions.

Key setup: need to seed `LegalDocument` + `DocumentVersion` with `RequiredForTeamId = SystemTeamIds.Volunteers` to make the consent check meaningful. Then seed `ConsentRecord` for users who should have consents.

Read `MembershipCalculator.cs` to understand how `GetUsersWithAllRequiredConsentsForTeamAsync` works — `PartitionUsersAsync` should call it internally.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Application.Tests --filter "MembershipPartition"`

- [ ] **Step 3: Implement PartitionUsersAsync in MembershipCalculator**

Implementation:

```
1. Convert input to List<Guid>
2. Load deletion flags: users where DeletionRequestedAt != null → pendingDeletion set
3. Remaining = input minus pendingDeletion
4. Load profiles for remaining users (single query with Include)
5. Users with no profile → incompleteSignup set
6. Remaining = remaining minus incompleteSignup
7. Users where IsSuspended → suspended set
8. Remaining = remaining minus suspended
9. Users where !IsApproved → pendingApproval set
10. Remaining = approved, not suspended users. Call existing:
    GetUsersWithAllRequiredConsentsForTeamAsync(remaining, SystemTeamIds.Volunteers)
    → returns those with all consents = active set
11. Remaining minus active = missingConsents set
12. Return MembershipPartition(all 6 sets)
```

Note: `SystemTeamIds.Volunteers` is the team ID constant used for Volunteers consent requirements. Read `src/Humans.Domain/Constants/SystemTeamIds.cs` for the value.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests --filter "MembershipPartition"`

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/MembershipCalculator.cs tests/Humans.Application.Tests/Services/MembershipPartitionTests.cs
git commit -m "feat(membership): implement PartitionUsersAsync with 9 tests"
```

### Task 3: Update Dashboard (AdminDashboardData + OnboardingService)

**Files:**
- Modify: `src/Humans.Application/DTOs/AdminDashboardData.cs`
- Modify: `src/Humans.Infrastructure/Services/OnboardingService.cs`

- [ ] **Step 1: Update AdminDashboardData record**

Replace current fields with partition-based counts:

```csharp
public record AdminDashboardData(
    int TotalMembers,
    int IncompleteSignup,
    int PendingApproval,
    int ActiveMembers,
    int MissingConsents,
    int Suspended,
    int PendingDeletion,
    int PendingApplications,
    int TotalApplications,
    int ApprovedApplications,
    int RejectedApplications,
    int ColaboradorApplied,
    int AsociadoApplied);
```

- [ ] **Step 2: Rewrite GetAdminDashboardAsync in OnboardingService**

Read the current implementation at `OnboardingService.cs:489`. Replace the ad-hoc count queries with:

```csharp
var allUserIds = await _dbContext.Users.Select(u => u.Id).ToListAsync(ct);
var partition = await _membershipCalculator.PartitionUsersAsync(allUserIds, ct);
```

Use `partition.Active.Count`, `partition.PendingApproval.Count`, etc. for the counts.

Keep the tier application stats queries unchanged (they're orthogonal to the partition).

Inject `IMembershipCalculator` into `OnboardingService` constructor if not already there. Read the constructor to check.

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`

Note: The Board dashboard view will break because it references old field names. That's expected — fixed in Task 5.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/DTOs/AdminDashboardData.cs src/Humans.Infrastructure/Services/OnboardingService.cs
git commit -m "feat(membership): rewrite dashboard data using shared partition"
```

### Task 4: Update Admin /Humans Filters (ProfileService)

**Files:**
- Modify: `src/Humans.Infrastructure/Services/ProfileService.cs`

- [ ] **Step 1: Rewrite GetFilteredHumansAsync**

Read current implementation at `ProfileService.cs:539-584`.

The status filter switch needs new cases. The challenge: the current implementation filters at the query level (EF → SQL), but `PartitionUsersAsync` works in memory. At ~500 users (eventually ~10k), we can:

1. For status filters that need partition (Active, MissingConsents): call `PartitionUsersAsync` for the relevant user IDs, then filter
2. For simple filters (Suspended, PendingApproval, IncompleteSignup, PendingDeletion): keep the EF Where clauses — they're unambiguous

**Approach:** For "active" and "missingconsents" filters, get the partition and filter by ID set. For the rest, use direct EF queries (faster, no consent check needed).

Update the status string in the Select projection to use the 6-bucket names.

Updated filter cases:
```csharp
case "active":
    var activePartition = await _membershipCalculator.PartitionUsersAsync(
        await query.Select(u => u.Id).ToListAsync(ct), ct);
    query = query.Where(u => activePartition.Active.Contains(u.Id));
    break;
case "missingconsents":
    var mcPartition = await _membershipCalculator.PartitionUsersAsync(
        await query.Select(u => u.Id).ToListAsync(ct), ct);
    query = query.Where(u => mcPartition.MissingConsents.Contains(u.Id));
    break;
case "pending":
    query = query.Where(u => u.Profile != null && !u.Profile.IsApproved && !u.Profile.IsSuspended);
    break;
case "suspended":
    query = query.Where(u => u.Profile != null && u.Profile.IsSuspended);
    break;
case "incomplete":
    query = query.Where(u => u.Profile == null);
    break;
case "deleting":
    query = query.Where(u => u.DeletionRequestedAt != null);
    break;
```

Also update the status string projection — the inline ternary that computes the status label. It currently doesn't account for MissingConsents or IncompleteSignup. Simplest: for "active" and "missingconsents" filtered views, the status is known. For unfiltered/other, compute via partition or use the existing ternary (which is "close enough" for display — the filter is what matters for correctness).

Actually, the cleaner approach: when no filter or a non-partition filter is used, compute the status label per-user using the partition. Call `PartitionUsersAsync` once for all users in the current page, then look up each user's bucket.

Inject `IMembershipCalculator` into `ProfileService` constructor if not already there.

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Services/ProfileService.cs
git commit -m "feat(membership): align admin filters with 6-bucket partition"
```

### Task 5: Update SystemTeamSyncJob

**Files:**
- Modify: `src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs`

- [ ] **Step 1: Update SyncVolunteersTeamAsync**

Read current implementation. Replace the inline consent check with partition:

```csharp
var allApprovedIds = await _dbContext.Profiles
    .AsNoTracking()
    .Where(p => p.IsApproved && !p.IsSuspended)
    .Select(p => p.UserId)
    .ToListAsync(cancellationToken);

var partition = await _membershipCalculator.PartitionUsersAsync(allApprovedIds, cancellationToken);
var eligibleUserIds = partition.Active.ToList();

await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken);
```

Inject `IMembershipCalculator` if not already injected. Check the constructor.

- [ ] **Step 2: Build and run tests**

Run: `dotnet build Humans.slnx && dotnet test tests/Humans.Application.Tests`

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs
git commit -m "feat(membership): use shared partition for Volunteers team eligibility"
```

### Task 6: Update Dashboard View

**Files:**
- Modify: `src/Humans.Web/Views/Board/Index.cshtml`

- [ ] **Step 1: Read the current dashboard view**

Find the section that shows the count cards. Update to show the 6 partition categories:
- Total Humans
- Active (green)
- Pending Approval (yellow)
- Missing Consents (orange) *(new)*
- Incomplete Signup (gray) *(new, renamed from implicit)*
- Suspended (red)
- Pending Deletion (dark/muted)

Each count should link to the admin /Humans page with the corresponding filter, e.g.:
```html
<a href="/Admin/Humans?status=active">224</a>
```

Keep the tier application stats below the partition cards (they're separate).

- [ ] **Step 2: Build and verify visually**

Run the app, check `/Board` dashboard. Verify counts sum to total.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Board/Index.cshtml
git commit -m "feat(membership): update dashboard to show 6 partition categories"
```

### Task 7: Update Admin /Humans View

**Files:**
- Modify: `src/Humans.Web/Views/Human/Humans.cshtml`

- [ ] **Step 1: Update status filter buttons**

Replace current filter buttons (All, Active, Pending, Suspended, Inactive, Deleting) with:
- All Statuses
- Active
- Pending Approval
- Missing Consents *(new)*
- Incomplete Signup *(renamed)*
- Suspended
- Pending Deletion *(renamed)*

Each button links to `?status=<value>` matching the switch cases in Task 4.

- [ ] **Step 2: Update status badge colors**

Map the new status labels to badge classes:
- Active → `bg-success`
- Pending Approval → `bg-warning text-dark`
- Missing Consents → `bg-orange` or `bg-warning` with distinct styling
- Incomplete Signup → `bg-secondary`
- Suspended → `bg-danger`
- Pending Deletion → `bg-dark`

- [ ] **Step 3: Build and verify visually**

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Human/Humans.cshtml
git commit -m "feat(membership): update admin humans page with 6 status filters and badges"
```

### Task 8: Feature Documentation + Final Tests

**Files:**
- Create or modify: `docs/features/XX-membership-status.md`
- Modify: `.claude/DATA_MODEL.md` (if status model is documented there)

- [ ] **Step 1: Write membership status feature doc**

Document the 6-bucket partition, the state diagram from the spec, which code computes it, and how to add a new status in the future.

- [ ] **Step 2: Run full test suite**

Run: `dotnet test tests/Humans.Application.Tests`
Expected: All pass

- [ ] **Step 3: Build the full solution**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```bash
git add docs/features/ .claude/DATA_MODEL.md
git commit -m "docs: add membership status partition documentation"
```
