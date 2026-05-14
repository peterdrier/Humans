# Ticket Attendee Contact Import — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a manually-triggered admin job that creates a no-profile Humans user for each unmatched ticket attendee, mirroring the Mailer plan/apply pattern with squatter protection and a per-row checkbox preview.

**Architecture:** New Application service `AttendeeContactImportService` orchestrates four primitives that already exist (`IAccountProvisioningService`, `IUserEmailService`, `IUserService`, `ITicketRepository`) and exposes `BuildPlanAsync` + `ApplyAsync` returning DTOs. Web layer adds a `TicketsContactsAdminController` with GET (preview) + POST (apply selected) routes under `/Tickets/Admin/Contacts`. No new entity, no migration.

**Tech Stack:** ASP.NET Core 10 (Razor + MVC), EF Core 10, NodaTime, xUnit + NSubstitute + AwesomeAssertions, NodaTime.Testing.

**Spec:** [`docs/superpowers/specs/2026-05-13-ticket-attendee-contact-import-design.md`](../specs/2026-05-13-ticket-attendee-contact-import-design.md)

---

## File Structure

**New files (Application layer):**
- `src/Humans.Application/Interfaces/Tickets/IAttendeeContactImportService.cs`
- `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportOutcome.cs`
- `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportDecision.cs`
- `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportPlan.cs`
- `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportResult.cs`
- `src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs`

**New files (Web layer):**
- `src/Humans.Web/Controllers/Tickets/TicketsContactsAdminController.cs`
- `src/Humans.Web/Models/Tickets/ContactImportPreviewViewModel.cs`
- `src/Humans.Web/Views/Tickets/Admin/Contacts.cshtml`

**Modified files:**
- `src/Humans.Domain/Enums/AuditAction.cs` — add `TicketContactsImported`
- `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs` — add `GetUnmatchedActiveAttendeesAsync`
- `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` — implement that method
- `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs` — add `InvalidateAfterContactImport`
- `src/Humans.Application/Services/Tickets/TicketQueryService.cs` — implement
- `src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs` — DI registration
- `src/Humans.Web/Views/Tickets/Index.cshtml` (or wherever the sync action lives) — add "Import Attendee Contacts" link
- `docs/sections/tickets.md` — section invariants, routing table, freshness triggers

**New test files:**
- `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs`
- `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceApplyTests.cs`
- `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceSquatterTests.cs`
- `tests/Humans.Application.Tests/Architecture/AttendeeContactImportArchitectureTests.cs`
- `tests/Humans.Web.Tests/Controllers/Tickets/TicketsContactsAdminControllerTests.cs`
- `tests/Humans.Infrastructure.Tests/Repositories/Tickets/TicketRepositoryUnmatchedAttendeesTests.cs` (or extend existing repo test file if one exists)

---

## Task 1: Add `TicketContactsImported` audit action

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Add the enum value**

Open `src/Humans.Domain/Enums/AuditAction.cs`. Find the existing `MailerLiteReconciliationCompleted` value (around line 166). Add a new value `TicketContactsImported` near it (preserve enum ordering conventions — append to end if values are append-only, otherwise group with other Ticket actions near `TicketTransferRequested`):

```csharp
TicketContactsImported,
```

- [ ] **Step 2: Build to verify no breakage**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds, no errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat: add TicketContactsImported audit action"
```

---

## Task 2: Add `AttendeeImportOutcome` enum

**Files:**
- Create: `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportOutcome.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// Per-attendee classification produced by
/// <see cref="Humans.Application.Interfaces.Tickets.IAttendeeContactImportService.BuildPlanAsync"/>.
/// Mirrors the Mailer import's <c>SubscriberOutcome</c> shape — verified
/// matches attach, unverified matches are deleted-then-created (squatter
/// protection), no match creates a new user with a verified UserEmail row.
/// </summary>
public enum AttendeeImportOutcome
{
    /// <summary>Exactly one verified UserEmail matches — set MatchedUserId, no creation.</summary>
    AttachVerified = 0,

    /// <summary>&gt;1 verified users own this email — skip with LogError (data-integrity).</summary>
    AmbiguousMultipleVerified = 1,

    /// <summary>Only an unverified UserEmail row matches — delete it, then create new user with verified row.</summary>
    DeleteUnverifiedThenCreate = 2,

    /// <summary>No UserEmail row matches — create a brand-new user with verified row.</summary>
    CreateNewUser = 3,

    /// <summary>Attendee has no email — skip.</summary>
    SkipNoEmail = 4,

    /// <summary>Attendee is Void — skip (typically excluded from plan input).</summary>
    SkipVoided = 5,
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportOutcome.cs
git commit -m "feat: add AttendeeImportOutcome enum"
```

---

## Task 3: Add `AttendeeImportDecision` and `AttendeeImportPlan` DTOs

**Files:**
- Create: `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportDecision.cs`
- Create: `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportPlan.cs`

- [ ] **Step 1: Create `AttendeeImportDecision`**

```csharp
namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// One per attendee in <see cref="AttendeeImportPlan"/>. Carries the
/// classification plus the data the apply step needs (target user id for
/// attach, unverified row id for delete-then-create, etc).
/// </summary>
/// <param name="AttendeeId">PK of the TicketAttendee row.</param>
/// <param name="Email">Attendee email (case preserved from vendor).</param>
/// <param name="AttendeeName">
/// Resolved display name: LegalName → FirstName+LastName → null fallback.
/// </param>
/// <param name="VendorTicketId">For cross-reference with the vendor dashboard.</param>
/// <param name="Outcome">Classification result.</param>
/// <param name="TargetUserId">
/// For <see cref="AttendeeImportOutcome.AttachVerified"/>: the live user id
/// (post tombstone follow). Null otherwise.
/// </param>
/// <param name="UnverifiedEmailIdToDelete">
/// For <see cref="AttendeeImportOutcome.DeleteUnverifiedThenCreate"/>: the
/// UserEmail row id to delete before provisioning. Null otherwise.
/// </param>
/// <param name="UnverifiedRowUserId">
/// For <see cref="AttendeeImportOutcome.DeleteUnverifiedThenCreate"/>: the
/// owning user id of the unverified row (DeleteEmailAsync requires both).
/// Null otherwise.
/// </param>
/// <param name="AmbiguousUserIds">
/// For <see cref="AttendeeImportOutcome.AmbiguousMultipleVerified"/>: the
/// conflicting user ids, for admin visibility. Null otherwise.
/// </param>
public sealed record AttendeeImportDecision(
    Guid AttendeeId,
    string? Email,
    string? AttendeeName,
    string VendorTicketId,
    AttendeeImportOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedEmailIdToDelete,
    Guid? UnverifiedRowUserId,
    IReadOnlyList<Guid>? AmbiguousUserIds);
```

- [ ] **Step 2: Create `AttendeeImportPlan`**

```csharp
namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// Output of <see cref="Humans.Application.Interfaces.Tickets.IAttendeeContactImportService.BuildPlanAsync"/>.
/// Stateless — the apply step re-queries to defend against concurrent sync mutation.
/// </summary>
public sealed record AttendeeImportPlan(
    IReadOnlyList<AttendeeImportDecision> Decisions,
    int TotalUnmatched)
{
    public AttendeeImportPlanCounts Counts => new(
        AttachVerified: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.AttachVerified),
        AmbiguousMultipleVerified: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.AmbiguousMultipleVerified),
        DeleteUnverifiedThenCreate: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.DeleteUnverifiedThenCreate),
        CreateNewUser: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.CreateNewUser),
        SkipNoEmail: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.SkipNoEmail),
        SkipVoided: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.SkipVoided));
}

public sealed record AttendeeImportPlanCounts(
    int AttachVerified,
    int AmbiguousMultipleVerified,
    int DeleteUnverifiedThenCreate,
    int CreateNewUser,
    int SkipNoEmail,
    int SkipVoided);
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportDecision.cs \
        src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportPlan.cs
git commit -m "feat: add AttendeeImportDecision and AttendeeImportPlan DTOs"
```

---

## Task 4: Add `AttendeeImportResult` DTO with `FormatSummary`

**Files:**
- Create: `src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportResult.cs`
- Create: `tests/Humans.Application.Tests/Services/Tickets/AttendeeImportResultTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets.Dtos;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeImportResultTests
{
    [HumansFact]
    public void FormatSummary_IncludesAllCounters()
    {
        var result = new AttendeeImportResult(
            TotalAttempted: 100,
            UsersCreated: 60,
            AttachedToExistingVerified: 30,
            UnverifiedRowsDeletedAndUserCreated: 5,
            AmbiguousSkipped: 2,
            NoEmailSkipped: 3,
            VanishedBetweenPlanAndApply: 1,
            Errors: 0,
            Elapsed: Duration.FromSeconds(12));

        var summary = result.FormatSummary();

        summary.Should().Contain("created=60");
        summary.Should().Contain("attached=30");
        summary.Should().Contain("unverified-replaced=5");
        summary.Should().Contain("ambiguous=2");
        summary.Should().Contain("no-email=3");
        summary.Should().Contain("vanished=1");
        summary.Should().Contain("errors=0");
        summary.Should().Contain("12000ms");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeImportResultTests"`
Expected: FAIL — `AttendeeImportResult` does not exist.

- [ ] **Step 3: Create the DTO**

```csharp
using NodaTime;

namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// Outcome counters from
/// <see cref="Humans.Application.Interfaces.Tickets.IAttendeeContactImportService.ApplyAsync"/>.
/// Format mirrors <c>ImportResult.FormatSummary</c> in the Mailer section so
/// admin banner / audit row reads consistently across import jobs.
/// </summary>
public sealed record AttendeeImportResult(
    int TotalAttempted,
    int UsersCreated,
    int AttachedToExistingVerified,
    int UnverifiedRowsDeletedAndUserCreated,
    int AmbiguousSkipped,
    int NoEmailSkipped,
    int VanishedBetweenPlanAndApply,
    int Errors,
    Duration Elapsed)
{
    public string FormatSummary() =>
        $"attempted={TotalAttempted}, created={UsersCreated}, attached={AttachedToExistingVerified}, " +
        $"unverified-replaced={UnverifiedRowsDeletedAndUserCreated}, ambiguous={AmbiguousSkipped}, " +
        $"no-email={NoEmailSkipped}, vanished={VanishedBetweenPlanAndApply}, errors={Errors}, " +
        $"elapsed={(long)Elapsed.TotalMilliseconds}ms";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeImportResultTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/Dtos/AttendeeImportResult.cs \
        tests/Humans.Application.Tests/Services/Tickets/AttendeeImportResultTests.cs
git commit -m "feat: add AttendeeImportResult DTO with formatted summary"
```

---

## Task 5: Add `IAttendeeContactImportService` interface

**Files:**
- Create: `src/Humans.Application/Interfaces/Tickets/IAttendeeContactImportService.cs`

- [ ] **Step 1: Create the interface**

```csharp
using Humans.Application.Interfaces.Tickets.Dtos;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Plan-and-apply import that creates Humans users for ticket attendees
/// whose email doesn't already resolve to an existing user. Mirrors the
/// Mailer import shape: <see cref="BuildPlanAsync"/> classifies, the admin
/// previews + selects, <see cref="ApplyAsync"/> executes only selected rows.
///
/// Stateless: <see cref="ApplyAsync"/> re-queries unmatched attendees so
/// plan and apply are independent (a sync in between is tolerated and
/// counted as <c>VanishedBetweenPlanAndApply</c>).
/// </summary>
public interface IAttendeeContactImportService : IApplicationService
{
    Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default);

    Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/IAttendeeContactImportService.cs
git commit -m "feat: add IAttendeeContactImportService interface"
```

---

## Task 6: Add `ITicketRepository.GetUnmatchedActiveAttendeesAsync` (integration-tested)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs`
- Create: `tests/Humans.Infrastructure.Tests/Repositories/Tickets/TicketRepositoryUnmatchedAttendeesTests.cs`

- [ ] **Step 1: Add the method to the interface**

Open `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs`. Near the existing `UpsertAttendeesAsync` method, add:

```csharp
    /// <summary>
    /// Returns every <see cref="TicketAttendee"/> for the given vendor event
    /// that is currently unmatched (<c>MatchedUserId is null</c>) and whose
    /// <see cref="Humans.Domain.Enums.TicketAttendeeStatus"/> is
    /// <c>Valid</c> or <c>CheckedIn</c>, AND whose
    /// <see cref="TicketAttendee.AttendeeEmail"/> is non-empty.
    ///
    /// Used by the attendee contact import to identify candidates for user
    /// provisioning. Returns the full entity so the caller can mutate
    /// <c>MatchedUserId</c> and pass the list back to
    /// <see cref="UpsertAttendeesAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TicketAttendee>> GetUnmatchedActiveAttendeesAsync(
        string vendorEventId, CancellationToken ct = default);
```

- [ ] **Step 2: Write a failing integration test**

Use the existing repo-test harness pattern (mirror `TicketRepositoryTests.cs` if present — its setup creates an in-memory `HumansDbContext`):

```csharp
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Tickets;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Infrastructure.Tests.Repositories.Tickets;

public class TicketRepositoryUnmatchedAttendeesTests
{
    [HumansFact]
    public async Task GetUnmatchedActiveAttendeesAsync_ReturnsOnlyValidAndCheckedIn_WithEmail_AndNullMatch()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new HumansDbContext(options);

        var orderId = Guid.NewGuid();
        var eventId = "evt_123";

        db.Set<TicketOrder>().Add(new TicketOrder
        {
            Id = orderId,
            VendorOrderId = "ord_1",
            VendorEventId = eventId,
            BuyerEmail = "buyer@x.com",
            BuyerName = "Buyer",
            TotalAmount = 0,
            Currency = "EUR",
            PaymentStatus = PaymentStatus.Paid,
            PurchasedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        });

        var includedId = Guid.NewGuid();
        db.Set<TicketAttendee>().AddRange(
            new TicketAttendee
            {
                Id = includedId, TicketOrderId = orderId, VendorEventId = eventId,
                VendorTicketId = "tkt_valid_unmatched", AttendeeEmail = "a@x.com",
                Status = TicketAttendeeStatus.Valid, MatchedUserId = null,
            },
            new TicketAttendee
            {
                Id = Guid.NewGuid(), TicketOrderId = orderId, VendorEventId = eventId,
                VendorTicketId = "tkt_voided", AttendeeEmail = "b@x.com",
                Status = TicketAttendeeStatus.Void, MatchedUserId = null,
            },
            new TicketAttendee
            {
                Id = Guid.NewGuid(), TicketOrderId = orderId, VendorEventId = eventId,
                VendorTicketId = "tkt_matched", AttendeeEmail = "c@x.com",
                Status = TicketAttendeeStatus.Valid, MatchedUserId = Guid.NewGuid(),
            },
            new TicketAttendee
            {
                Id = Guid.NewGuid(), TicketOrderId = orderId, VendorEventId = eventId,
                VendorTicketId = "tkt_no_email", AttendeeEmail = null,
                Status = TicketAttendeeStatus.Valid, MatchedUserId = null,
            },
            new TicketAttendee
            {
                Id = Guid.NewGuid(), TicketOrderId = orderId, VendorEventId = "other_event",
                VendorTicketId = "tkt_other_event", AttendeeEmail = "d@x.com",
                Status = TicketAttendeeStatus.Valid, MatchedUserId = null,
            });
        await db.SaveChangesAsync();

        var repo = new TicketRepository(db);

        var result = await repo.GetUnmatchedActiveAttendeesAsync(eventId);

        result.Should().HaveCount(1);
        result.Single().Id.Should().Be(includedId);
    }

    [HumansFact]
    public async Task GetUnmatchedActiveAttendeesAsync_IncludesCheckedInAttendees()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new HumansDbContext(options);

        var orderId = Guid.NewGuid();
        var eventId = "evt_123";
        db.Set<TicketOrder>().Add(new TicketOrder
        {
            Id = orderId, VendorOrderId = "ord_1", VendorEventId = eventId,
            BuyerEmail = "buyer@x.com", BuyerName = "Buyer", TotalAmount = 0,
            Currency = "EUR", PaymentStatus = PaymentStatus.Paid,
            PurchasedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        });

        var includedId = Guid.NewGuid();
        db.Set<TicketAttendee>().Add(new TicketAttendee
        {
            Id = includedId, TicketOrderId = orderId, VendorEventId = eventId,
            VendorTicketId = "tkt_ci", AttendeeEmail = "ci@x.com",
            Status = TicketAttendeeStatus.CheckedIn, MatchedUserId = null,
        });
        await db.SaveChangesAsync();

        var repo = new TicketRepository(db);
        var result = await repo.GetUnmatchedActiveAttendeesAsync(eventId);

        result.Should().ContainSingle(a => a.Id == includedId);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketRepositoryUnmatchedAttendeesTests"`
Expected: FAIL — method not defined.

- [ ] **Step 4: Implement on the repository**

Open `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs`. Add the implementation alongside other attendee methods:

```csharp
public Task<IReadOnlyList<TicketAttendee>> GetUnmatchedActiveAttendeesAsync(
    string vendorEventId, CancellationToken ct = default) =>
    _db.Set<TicketAttendee>()
        .Where(a =>
            a.VendorEventId == vendorEventId &&
            a.MatchedUserId == null &&
            !string.IsNullOrEmpty(a.AttendeeEmail) &&
            (a.Status == TicketAttendeeStatus.Valid ||
             a.Status == TicketAttendeeStatus.CheckedIn))
        .ToListAsync(ct)
        .ContinueWith(t => (IReadOnlyList<TicketAttendee>)t.Result, ct);
```

(If the file's existing methods use a different pattern — e.g. `async` with `await` — match that style. The `.ContinueWith` form is a fallback only if the file is consistently `Task<T>` returning, which most EF repos in this codebase are not. Default to `async`/`await`:)

```csharp
public async Task<IReadOnlyList<TicketAttendee>> GetUnmatchedActiveAttendeesAsync(
    string vendorEventId, CancellationToken ct = default)
{
    return await _db.Set<TicketAttendee>()
        .Where(a =>
            a.VendorEventId == vendorEventId &&
            a.MatchedUserId == null &&
            !string.IsNullOrEmpty(a.AttendeeEmail) &&
            (a.Status == TicketAttendeeStatus.Valid ||
             a.Status == TicketAttendeeStatus.CheckedIn))
        .ToListAsync(ct);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketRepositoryUnmatchedAttendeesTests"`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs \
        src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs \
        tests/Humans.Infrastructure.Tests/Repositories/Tickets/TicketRepositoryUnmatchedAttendeesTests.cs
git commit -m "feat: ITicketRepository.GetUnmatchedActiveAttendeesAsync for contact import"
```

---

## Task 7: Add `ITicketQueryService.InvalidateAfterContactImport`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs`
- Modify: `src/Humans.Application/Services/Tickets/TicketQueryService.cs`

- [ ] **Step 1: Add to the interface**

Open `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs`. Near the existing `InvalidateAfterTransfer` declaration (around line 199), add:

```csharp
    /// <summary>
    /// Invalidates ticket-related caches after the attendee contact import
    /// has applied new matches. Affects: <c>UserIdsWithTickets</c>,
    /// <c>ValidAttendeeEmails</c>, the per-event <c>TicketEventSummary</c>,
    /// and <c>TicketDashboardStats</c> (invalidator-only key). Per-user
    /// <c>UserTicketCount:{userId}</c> entries are not enumerable and expire
    /// naturally via 5-minute TTL — same policy as the sync.
    /// </summary>
    void InvalidateAfterContactImport();
```

- [ ] **Step 2: Implement it**

Open `src/Humans.Application/Services/Tickets/TicketQueryService.cs`. Find the existing `InvalidateAfterTransfer` implementation. Add:

```csharp
public void InvalidateAfterContactImport()
{
    _cache.InvalidateTicketCaches();
}
```

(Use the same `_cache.InvalidateTicketCaches()` seam used by `TicketSyncService`. Look at how `InvalidateAfterTransfer` is written and mirror it — single-line delegation is fine.)

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs \
        src/Humans.Application/Services/Tickets/TicketQueryService.cs
git commit -m "feat: ITicketQueryService.InvalidateAfterContactImport seam"
```

---

## Task 8: Scaffold `AttendeeContactImportService` + plan-classifier harness

**Files:**
- Create: `src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs`
- Create: `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs`

- [ ] **Step 1: Write the first failing test — SkipNoEmail**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeContactImportServicePlanTests
{
    [HumansFact]
    public async Task Plan_AttendeeWithoutEmail_ClassifiedAsSkipNoEmail()
    {
        var harness = new PlanHarness();
        harness.AddUnmatched(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = "tkt_1",
            AttendeeEmail = null,
            FirstName = "Jane",
            LastName = "Doe",
            Status = TicketAttendeeStatus.Valid,
        });

        var plan = await harness.Service.BuildPlanAsync();

        plan.Decisions.Should().ContainSingle()
            .Which.Outcome.Should().Be(AttendeeImportOutcome.SkipNoEmail);
    }
}

internal sealed class PlanHarness
{
    public ITicketRepository TicketRepo { get; } = Substitute.For<ITicketRepository>();
    public IUserEmailService UserEmails { get; } = Substitute.For<IUserEmailService>();
    public IAccountProvisioningService Provisioning { get; } = Substitute.For<IAccountProvisioningService>();
    public IUserService Users { get; } = Substitute.For<IUserService>();
    public IShiftManagementService Shifts { get; } = Substitute.For<IShiftManagementService>();
    public ITicketQueryService TicketQuery { get; } = Substitute.For<ITicketQueryService>();
    public IAuditLogService Audit { get; } = Substitute.For<IAuditLogService>();
    public FakeClock Clock { get; } = new(Instant.FromUtc(2026, 5, 13, 12, 0));

    private readonly List<TicketAttendee> _unmatched = new();

    public PlanHarness()
    {
        TicketRepo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "evt_active" });
        TicketRepo.GetUnmatchedActiveAttendeesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => _unmatched);
    }

    public void AddUnmatched(TicketAttendee a) => _unmatched.Add(a);

    public AttendeeContactImportService Service => new(
        TicketRepo, UserEmails, Provisioning, Users, Shifts, TicketQuery, Audit, Clock,
        NullLogger<AttendeeContactImportService>.Instance);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServicePlanTests"`
Expected: FAIL — `AttendeeContactImportService` does not exist.

- [ ] **Step 3: Scaffold the service with just enough to make the test pass**

```csharp
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Tickets;

public sealed class AttendeeContactImportService : IAttendeeContactImportService
{
    private readonly ITicketRepository _ticketRepository;
    private readonly IUserEmailService _userEmails;
    private readonly IAccountProvisioningService _provisioning;
    private readonly IUserService _users;
    private readonly IShiftManagementService _shifts;
    private readonly ITicketQueryService _ticketQuery;
    private readonly IAuditLogService _audit;
    private readonly IClock _clock;
    private readonly ILogger<AttendeeContactImportService> _logger;

    public AttendeeContactImportService(
        ITicketRepository ticketRepository,
        IUserEmailService userEmails,
        IAccountProvisioningService provisioning,
        IUserService users,
        IShiftManagementService shifts,
        ITicketQueryService ticketQuery,
        IAuditLogService audit,
        IClock clock,
        ILogger<AttendeeContactImportService> logger)
    {
        _ticketRepository = ticketRepository;
        _userEmails = userEmails;
        _provisioning = provisioning;
        _users = users;
        _shifts = shifts;
        _ticketQuery = ticketQuery;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default)
    {
        var state = await _ticketRepository.GetSyncStateAsync(ct);
        var eventId = state?.VendorEventId
            ?? throw new InvalidOperationException("No active vendor event id — sync has not run.");

        var unmatched = await _ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
        var decisions = new List<AttendeeImportDecision>(unmatched.Count);

        foreach (var a in unmatched)
        {
            decisions.Add(await ClassifyAsync(a, ct));
        }

        return new AttendeeImportPlan(decisions, unmatched.Count);
    }

    public Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        CancellationToken ct = default) =>
        throw new NotImplementedException("Filled in by Task 11");

    private async Task<AttendeeImportDecision> ClassifyAsync(TicketAttendee a, CancellationToken ct)
    {
        var name = ResolveDisplayName(a);

        if (string.IsNullOrWhiteSpace(a.AttendeeEmail))
        {
            return new AttendeeImportDecision(
                a.Id, a.AttendeeEmail, name, a.VendorTicketId,
                AttendeeImportOutcome.SkipNoEmail,
                TargetUserId: null,
                UnverifiedEmailIdToDelete: null,
                UnverifiedRowUserId: null,
                AmbiguousUserIds: null);
        }

        // Remaining classifications filled in by Tasks 9–10.
        throw new NotImplementedException("Branches filled in by Tasks 9–10");
    }

    private static string? ResolveDisplayName(TicketAttendee a)
    {
        if (!string.IsNullOrWhiteSpace(a.LegalName)) return a.LegalName;
        var first = a.FirstName?.Trim();
        var last = a.LastName?.Trim();
        var combined = $"{first} {last}".Trim();
        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServicePlanTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs \
        tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs
git commit -m "feat: AttendeeContactImportService scaffold + SkipNoEmail classifier"
```

---

## Task 9: Add `AttachVerified` and `AmbiguousMultipleVerified` classifiers

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs`
- Modify: `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs`

- [ ] **Step 1: Add the failing tests**

Append to `AttendeeContactImportServicePlanTests`:

```csharp
[HumansFact]
public async Task Plan_SingleVerifiedMatch_ClassifiedAsAttachVerified()
{
    var harness = new PlanHarness();
    var userId = Guid.NewGuid();
    harness.AddUnmatched(new TicketAttendee
    {
        Id = Guid.NewGuid(), VendorTicketId = "tkt_v",
        AttendeeEmail = "jane@x.com", FirstName = "Jane", LastName = "Doe",
        Status = TicketAttendeeStatus.Valid,
    });
    harness.UserEmails.GetDistinctVerifiedUserIdsAsync("jane@x.com", Arg.Any<CancellationToken>())
        .Returns(new[] { userId });
    harness.Users.GetByIdAsync(userId, Arg.Any<CancellationToken>())
        .Returns(new User { Id = userId, MergedToUserId = null });

    var plan = await harness.Service.BuildPlanAsync();

    var decision = plan.Decisions.Single();
    decision.Outcome.Should().Be(AttendeeImportOutcome.AttachVerified);
    decision.TargetUserId.Should().Be(userId);
    decision.AttendeeName.Should().Be("Jane Doe");
}

[HumansFact]
public async Task Plan_AttachVerified_FollowsMergedTombstone()
{
    var harness = new PlanHarness();
    var deadId = Guid.NewGuid();
    var liveId = Guid.NewGuid();
    harness.AddUnmatched(new TicketAttendee
    {
        Id = Guid.NewGuid(), VendorTicketId = "tkt_v",
        AttendeeEmail = "jane@x.com", FirstName = "Jane",
        Status = TicketAttendeeStatus.Valid,
    });
    harness.UserEmails.GetDistinctVerifiedUserIdsAsync("jane@x.com", Arg.Any<CancellationToken>())
        .Returns(new[] { deadId });
    harness.Users.GetByIdAsync(deadId, Arg.Any<CancellationToken>())
        .Returns(new User { Id = deadId, MergedToUserId = liveId });
    harness.Users.GetByIdAsync(liveId, Arg.Any<CancellationToken>())
        .Returns(new User { Id = liveId, MergedToUserId = null });

    var plan = await harness.Service.BuildPlanAsync();

    plan.Decisions.Single().TargetUserId.Should().Be(liveId);
}

[HumansFact]
public async Task Plan_MultipleVerifiedMatches_ClassifiedAsAmbiguous()
{
    var harness = new PlanHarness();
    var u1 = Guid.NewGuid();
    var u2 = Guid.NewGuid();
    harness.AddUnmatched(new TicketAttendee
    {
        Id = Guid.NewGuid(), VendorTicketId = "tkt_v",
        AttendeeEmail = "shared@x.com", Status = TicketAttendeeStatus.Valid,
    });
    harness.UserEmails.GetDistinctVerifiedUserIdsAsync("shared@x.com", Arg.Any<CancellationToken>())
        .Returns(new[] { u1, u2 });

    var plan = await harness.Service.BuildPlanAsync();

    var decision = plan.Decisions.Single();
    decision.Outcome.Should().Be(AttendeeImportOutcome.AmbiguousMultipleVerified);
    decision.AmbiguousUserIds.Should().BeEquivalentTo(new[] { u1, u2 });
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServicePlanTests"`
Expected: 3 new tests FAIL with `NotImplementedException` from the classifier.

- [ ] **Step 3: Implement the classifier branches**

Replace the `throw new NotImplementedException(...)` at the end of `ClassifyAsync` with:

```csharp
var verifiedUserIds = await _userEmails.GetDistinctVerifiedUserIdsAsync(a.AttendeeEmail!, ct);

if (verifiedUserIds.Count > 1)
{
    return new AttendeeImportDecision(
        a.Id, a.AttendeeEmail, name, a.VendorTicketId,
        AttendeeImportOutcome.AmbiguousMultipleVerified,
        TargetUserId: null,
        UnverifiedEmailIdToDelete: null,
        UnverifiedRowUserId: null,
        AmbiguousUserIds: verifiedUserIds);
}

if (verifiedUserIds.Count == 1)
{
    var liveTarget = await ResolveTombstoneAsync(verifiedUserIds[0], ct);
    return new AttendeeImportDecision(
        a.Id, a.AttendeeEmail, name, a.VendorTicketId,
        AttendeeImportOutcome.AttachVerified,
        TargetUserId: liveTarget,
        UnverifiedEmailIdToDelete: null,
        UnverifiedRowUserId: null,
        AmbiguousUserIds: null);
}

// Remaining classifications filled in by Task 10.
throw new NotImplementedException("Unverified/no-match branches filled in by Task 10");
```

And add the tombstone helper (copy the shape from `MailerImportService.ResolveTombstoneAsync`):

```csharp
private async Task<Guid> ResolveTombstoneAsync(Guid userId, CancellationToken ct)
{
    var visited = new HashSet<Guid> { userId };
    var current = userId;
    while (true)
    {
        var user = await _users.GetByIdAsync(current, ct);
        if (user?.MergedToUserId is not Guid next) return current;
        if (!visited.Add(next)) return current;
        current = next;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServicePlanTests"`
Expected: all 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs \
        tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs
git commit -m "feat: AttachVerified + AmbiguousMultipleVerified classifiers + tombstone follow"
```

---

## Task 10: Add `DeleteUnverifiedThenCreate` and `CreateNewUser` classifiers

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs`
- Modify: `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs`

- [ ] **Step 1: Add the failing tests**

```csharp
[HumansFact]
public async Task Plan_UnverifiedMatchOnly_ClassifiedAsDeleteUnverifiedThenCreate()
{
    var harness = new PlanHarness();
    var squatterUserId = Guid.NewGuid();
    var unverifiedRowId = Guid.NewGuid();
    harness.AddUnmatched(new TicketAttendee
    {
        Id = Guid.NewGuid(), VendorTicketId = "tkt_v",
        AttendeeEmail = "victim@x.com", FirstName = "Victim",
        Status = TicketAttendeeStatus.Valid,
    });
    harness.UserEmails.GetDistinctVerifiedUserIdsAsync("victim@x.com", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<Guid>());
    harness.UserEmails.FindAnyEmailRowByAddressAsync("victim@x.com", Arg.Any<CancellationToken>())
        .Returns((squatterUserId, unverifiedRowId));

    var plan = await harness.Service.BuildPlanAsync();

    var decision = plan.Decisions.Single();
    decision.Outcome.Should().Be(AttendeeImportOutcome.DeleteUnverifiedThenCreate);
    decision.UnverifiedRowUserId.Should().Be(squatterUserId);
    decision.UnverifiedEmailIdToDelete.Should().Be(unverifiedRowId);
}

[HumansFact]
public async Task Plan_NoUserEmailMatch_ClassifiedAsCreateNewUser()
{
    var harness = new PlanHarness();
    harness.AddUnmatched(new TicketAttendee
    {
        Id = Guid.NewGuid(), VendorTicketId = "tkt_v",
        AttendeeEmail = "fresh@x.com", FirstName = "Fresh", LastName = "Face",
        Status = TicketAttendeeStatus.Valid,
    });
    harness.UserEmails.GetDistinctVerifiedUserIdsAsync("fresh@x.com", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<Guid>());
    harness.UserEmails.FindAnyEmailRowByAddressAsync("fresh@x.com", Arg.Any<CancellationToken>())
        .Returns(((Guid, Guid)?)null);

    var plan = await harness.Service.BuildPlanAsync();

    var decision = plan.Decisions.Single();
    decision.Outcome.Should().Be(AttendeeImportOutcome.CreateNewUser);
    decision.AttendeeName.Should().Be("Fresh Face");
}

[HumansFact]
public async Task Plan_DisplayName_PrefersLegalName()
{
    var harness = new PlanHarness();
    harness.AddUnmatched(new TicketAttendee
    {
        Id = Guid.NewGuid(), VendorTicketId = "tkt_v",
        AttendeeEmail = "jane@x.com",
        LegalName = "Jane Q. Doe", FirstName = "Jane", LastName = "Doe",
        Status = TicketAttendeeStatus.Valid,
    });
    harness.UserEmails.GetDistinctVerifiedUserIdsAsync("jane@x.com", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<Guid>());
    harness.UserEmails.FindAnyEmailRowByAddressAsync("jane@x.com", Arg.Any<CancellationToken>())
        .Returns(((Guid, Guid)?)null);

    var plan = await harness.Service.BuildPlanAsync();

    plan.Decisions.Single().AttendeeName.Should().Be("Jane Q. Doe");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServicePlanTests"`
Expected: 3 new tests FAIL.

- [ ] **Step 3: Replace the trailing `throw` with the final branches**

Replace the trailing `throw new NotImplementedException("Unverified/no-match branches filled in by Task 10");` in `ClassifyAsync` with:

```csharp
var existingRow = await _userEmails.FindAnyEmailRowByAddressAsync(a.AttendeeEmail!, ct);
if (existingRow is var (uid, emailId))
{
    return new AttendeeImportDecision(
        a.Id, a.AttendeeEmail, name, a.VendorTicketId,
        AttendeeImportOutcome.DeleteUnverifiedThenCreate,
        TargetUserId: null,
        UnverifiedEmailIdToDelete: emailId,
        UnverifiedRowUserId: uid,
        AmbiguousUserIds: null);
}

return new AttendeeImportDecision(
    a.Id, a.AttendeeEmail, name, a.VendorTicketId,
    AttendeeImportOutcome.CreateNewUser,
    TargetUserId: null,
    UnverifiedEmailIdToDelete: null,
    UnverifiedRowUserId: null,
    AmbiguousUserIds: null);
```

- [ ] **Step 4: Run all plan tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServicePlanTests"`
Expected: all 7 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs \
        tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServicePlanTests.cs
git commit -m "feat: DeleteUnverifiedThenCreate + CreateNewUser classifiers"
```

---

## Task 11: Implement `ApplyAsync` — happy paths

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs`
- Create: `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceApplyTests.cs`

- [ ] **Step 1: Write failing tests for the three positive outcomes**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeContactImportServiceApplyTests
{
    [HumansFact]
    public async Task Apply_AttachVerified_SetsMatchedUserIdAndUpserts()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "jane@x.com", Status = TicketAttendeeStatus.Valid,
            MatchedUserId = null,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "jane@x.com", "Jane Doe", "tkt_v",
                    AttendeeImportOutcome.AttachVerified,
                    TargetUserId: targetUserId,
                    UnverifiedEmailIdToDelete: null,
                    UnverifiedRowUserId: null,
                    AmbiguousUserIds: null),
            },
            TotalUnmatched: 1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        result.AttachedToExistingVerified.Should().Be(1);
        result.UsersCreated.Should().Be(0);
        attendee.MatchedUserId.Should().Be(targetUserId);

        await harness.TicketRepo.Received(1)
            .UpsertAttendeesAsync(Arg.Is<IReadOnlyList<TicketAttendee>>(
                l => l.Count == 1 && l[0].Id == attendeeId), Arg.Any<CancellationToken>());

        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            targetUserId, 2026, ParticipationStatus.Ticketed, Arg.Any<CancellationToken>());

        harness.TicketQuery.Received(1).InvalidateAfterContactImport();

        await harness.Audit.Received(1).LogAsync(
            AuditAction.TicketContactsImported,
            "Tickets", Guid.Empty,
            Arg.Is<string>(s => s.Contains("attached=1")),
            nameof(AttendeeContactImportService));
    }

    [HumansFact]
    public async Task Apply_CreateNewUser_CallsProvisioningWithAttendeeName_AndTicketTailorSource()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "fresh@x.com", Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "fresh@x.com", "Fresh Face", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(
                new User { Id = newUserId }, Created: true));

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "fresh@x.com", "Fresh Face", "tkt_v",
                    AttendeeImportOutcome.CreateNewUser,
                    null, null, null, null),
            },
            1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        result.UsersCreated.Should().Be(1);
        attendee.MatchedUserId.Should().Be(newUserId);

        await harness.Provisioning.Received(1).FindOrCreateUserByEmailAsync(
            "fresh@x.com", "Fresh Face", ContactSource.TicketTailor, Arg.Any<CancellationToken>());

        await harness.Users.Received(1).SetParticipationFromTicketSyncAsync(
            newUserId, 2026, ParticipationStatus.Ticketed, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_DeleteUnverifiedThenCreate_DeletesSquatterRowFirst()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var squatterId = Guid.NewGuid();
        var squatterEmailId = Guid.NewGuid();
        var newUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "victim@x.com", Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", "Victim", ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newUserId }, true));

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "victim@x.com", "Victim", "tkt_v",
                    AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                    TargetUserId: null,
                    UnverifiedEmailIdToDelete: squatterEmailId,
                    UnverifiedRowUserId: squatterId,
                    AmbiguousUserIds: null),
            },
            1);

        var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        result.UnverifiedRowsDeletedAndUserCreated.Should().Be(1);
        result.UsersCreated.Should().Be(1);
        attendee.MatchedUserId.Should().Be(newUserId);

        Received.InOrder(() =>
        {
            harness.UserEmails.DeleteEmailAsync(squatterId, squatterEmailId, Arg.Any<CancellationToken>());
            harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", "Victim", ContactSource.TicketTailor, Arg.Any<CancellationToken>());
        });
    }
}

internal sealed class ApplyHarness
{
    public ITicketRepository TicketRepo { get; } = Substitute.For<ITicketRepository>();
    public IUserEmailService UserEmails { get; } = Substitute.For<IUserEmailService>();
    public IAccountProvisioningService Provisioning { get; } = Substitute.For<IAccountProvisioningService>();
    public IUserService Users { get; } = Substitute.For<IUserService>();
    public IShiftManagementService Shifts { get; } = Substitute.For<IShiftManagementService>();
    public ITicketQueryService TicketQuery { get; } = Substitute.For<ITicketQueryService>();
    public IAuditLogService Audit { get; } = Substitute.For<IAuditLogService>();
    public FakeClock Clock { get; } = new(Instant.FromUtc(2026, 5, 13, 12, 0));

    private readonly List<TicketAttendee> _unmatched = new();

    public ApplyHarness()
    {
        TicketRepo.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new TicketSyncState { Id = 1, VendorEventId = "evt_active" });
        TicketRepo.GetUnmatchedActiveAttendeesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => _unmatched);
    }

    public void WithUnmatched(TicketAttendee a) => _unmatched.Add(a);

    public void WithActiveYear(int year)
    {
        // Mirror whatever IShiftManagementService.GetActiveAsync returns —
        // a record with a Year property. Inspect the interface and adjust the
        // type below if needed; the property name "Year" is what matters.
        Shifts.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new ActiveEventInfo(year));
    }

    public AttendeeContactImportService Service => new(
        TicketRepo, UserEmails, Provisioning, Users, Shifts, TicketQuery, Audit, Clock,
        NullLogger<AttendeeContactImportService>.Instance);
}

// Local stand-in for whatever record IShiftManagementService.GetActiveAsync
// returns. Replace with the real type during impl if its name differs.
internal sealed record ActiveEventInfo(int Year);
```

> **Note for the implementer:** open `IShiftManagementService.cs` and use the real return type for `GetActiveAsync` — adjust `ActiveEventInfo` accordingly (probably an existing entity/DTO with a `.Year` int). The test compiles only after this resolution.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServiceApplyTests"`
Expected: FAIL — `ApplyAsync` throws NotImplementedException.

- [ ] **Step 3: Implement `ApplyAsync` (full body)**

Replace the throwing `ApplyAsync` body in `AttendeeContactImportService` with:

```csharp
public async Task<AttendeeImportResult> ApplyAsync(
    AttendeeImportPlan plan,
    IReadOnlySet<Guid> selectedAttendeeIds,
    CancellationToken ct = default)
{
    var start = _clock.GetCurrentInstant();

    var state = await _ticketRepository.GetSyncStateAsync(ct);
    var eventId = state?.VendorEventId
        ?? throw new InvalidOperationException("No active vendor event id — sync has not run.");

    // Re-query so plan/apply are stateless (a sync between plan and apply is tolerated).
    var freshUnmatched = await _ticketRepository.GetUnmatchedActiveAttendeesAsync(eventId, ct);
    var freshById = freshUnmatched.ToDictionary(a => a.Id);

    var toUpsert = new List<TicketAttendee>();
    var newlyMatchedUserIds = new HashSet<Guid>();
    int attempted = 0, created = 0, attached = 0, replaced = 0,
        ambiguous = 0, noEmail = 0, vanished = 0, errors = 0;

    foreach (var d in plan.Decisions.Where(d => selectedAttendeeIds.Contains(d.AttendeeId)))
    {
        attempted++;

        if (!freshById.TryGetValue(d.AttendeeId, out var attendee))
        {
            vanished++;
            _logger.LogInformation(
                "Attendee {AttendeeId} ({Email}) vanished between plan and apply",
                d.AttendeeId, d.Email);
            continue;
        }

        try
        {
            switch (d.Outcome)
            {
                case AttendeeImportOutcome.SkipNoEmail:
                    noEmail++;
                    break;

                case AttendeeImportOutcome.SkipVoided:
                    break;

                case AttendeeImportOutcome.AmbiguousMultipleVerified:
                    ambiguous++;
                    _logger.LogError(
                        "Attendee {AttendeeId} email {Email} verified by multiple users {UserIds}",
                        d.AttendeeId, d.Email, d.AmbiguousUserIds);
                    break;

                case AttendeeImportOutcome.AttachVerified:
                {
                    attendee.MatchedUserId = d.TargetUserId!.Value;
                    toUpsert.Add(attendee);
                    newlyMatchedUserIds.Add(d.TargetUserId.Value);
                    attached++;
                    break;
                }

                case AttendeeImportOutcome.DeleteUnverifiedThenCreate:
                {
                    if (d.UnverifiedRowUserId is Guid uid &&
                        d.UnverifiedEmailIdToDelete is Guid eid)
                    {
                        await _userEmails.DeleteEmailAsync(uid, eid, ct);
                    }
                    var (newUser, wasCreated) = await _provisioning.FindOrCreateUserByEmailAsync(
                        d.Email!, d.AttendeeName, ContactSource.TicketTailor, ct);
                    attendee.MatchedUserId = newUser.Id;
                    toUpsert.Add(attendee);
                    newlyMatchedUserIds.Add(newUser.Id);
                    if (wasCreated) created++;
                    replaced++;
                    break;
                }

                case AttendeeImportOutcome.CreateNewUser:
                {
                    var (newUser, wasCreated) = await _provisioning.FindOrCreateUserByEmailAsync(
                        d.Email!, d.AttendeeName, ContactSource.TicketTailor, ct);
                    attendee.MatchedUserId = newUser.Id;
                    toUpsert.Add(attendee);
                    newlyMatchedUserIds.Add(newUser.Id);
                    if (wasCreated) created++;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors++;
            _logger.LogError(ex,
                "Attendee contact import failed for {AttendeeId} ({Email})",
                d.AttendeeId, d.Email);
        }
    }

    if (toUpsert.Count > 0)
    {
        await _ticketRepository.UpsertAttendeesAsync(toUpsert, ct);
    }

    var active = await _shifts.GetActiveAsync(ct);
    if (active is not null && newlyMatchedUserIds.Count > 0)
    {
        foreach (var userId in newlyMatchedUserIds)
        {
            await _users.SetParticipationFromTicketSyncAsync(
                userId, active.Year, ParticipationStatus.Ticketed, ct);
        }
    }

    _ticketQuery.InvalidateAfterContactImport();

    var elapsed = _clock.GetCurrentInstant() - start;
    var result = new AttendeeImportResult(
        TotalAttempted: attempted,
        UsersCreated: created,
        AttachedToExistingVerified: attached,
        UnverifiedRowsDeletedAndUserCreated: replaced,
        AmbiguousSkipped: ambiguous,
        NoEmailSkipped: noEmail,
        VanishedBetweenPlanAndApply: vanished,
        Errors: errors,
        Elapsed: elapsed);

    await _audit.LogAsync(
        AuditAction.TicketContactsImported,
        entityType: "Tickets", entityId: Guid.Empty,
        description: result.FormatSummary(),
        jobName: nameof(AttendeeContactImportService));

    return result;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServiceApplyTests"`
Expected: 3 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs \
        tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceApplyTests.cs
git commit -m "feat: AttendeeContactImportService.ApplyAsync — happy paths"
```

---

## Task 12: Apply edge cases — selection filter, vanished, errors don't abort

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceApplyTests.cs`

- [ ] **Step 1: Add edge-case tests**

```csharp
[HumansFact]
public async Task Apply_OnlyProcessesSelectedAttendees()
{
    var harness = new ApplyHarness();
    var pickedId = Guid.NewGuid();
    var skippedId = Guid.NewGuid();
    var picked = new TicketAttendee
    {
        Id = pickedId, VendorTicketId = "tkt_p", VendorEventId = "evt_active",
        AttendeeEmail = "p@x.com", Status = TicketAttendeeStatus.Valid,
    };
    var unselected = new TicketAttendee
    {
        Id = skippedId, VendorTicketId = "tkt_s", VendorEventId = "evt_active",
        AttendeeEmail = "s@x.com", Status = TicketAttendeeStatus.Valid,
    };
    harness.WithUnmatched(picked);
    harness.WithUnmatched(unselected);
    harness.WithActiveYear(2026);
    harness.Provisioning.FindOrCreateUserByEmailAsync(
            Arg.Any<string>(), Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
        .Returns(ci => new AccountProvisioningResult(new User { Id = Guid.NewGuid() }, true));

    var plan = new AttendeeImportPlan(
        new[]
        {
            new AttendeeImportDecision(pickedId, "p@x.com", "P", "tkt_p",
                AttendeeImportOutcome.CreateNewUser, null, null, null, null),
            new AttendeeImportDecision(skippedId, "s@x.com", "S", "tkt_s",
                AttendeeImportOutcome.CreateNewUser, null, null, null, null),
        }, 2);

    var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { pickedId });

    result.TotalAttempted.Should().Be(1);
    result.UsersCreated.Should().Be(1);
    picked.MatchedUserId.Should().NotBeNull();
    unselected.MatchedUserId.Should().BeNull();
}

[HumansFact]
public async Task Apply_AttendeeVanishedBetweenPlanAndApply_IsCountedAndSkipped()
{
    var harness = new ApplyHarness();
    var goneId = Guid.NewGuid();
    // No call to harness.WithUnmatched — the re-query returns empty.
    harness.WithActiveYear(2026);

    var plan = new AttendeeImportPlan(
        new[]
        {
            new AttendeeImportDecision(goneId, "g@x.com", "G", "tkt_g",
                AttendeeImportOutcome.CreateNewUser, null, null, null, null),
        }, 1);

    var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { goneId });

    result.VanishedBetweenPlanAndApply.Should().Be(1);
    result.UsersCreated.Should().Be(0);

    await harness.Provisioning.DidNotReceive().FindOrCreateUserByEmailAsync(
        Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<ContactSource>(), Arg.Any<CancellationToken>());
}

[HumansFact]
public async Task Apply_PerAttendeeFailure_DoesNotAbortBatch()
{
    var harness = new ApplyHarness();
    var failId = Guid.NewGuid();
    var okId = Guid.NewGuid();
    harness.WithUnmatched(new TicketAttendee
    {
        Id = failId, VendorTicketId = "tkt_f", VendorEventId = "evt_active",
        AttendeeEmail = "f@x.com", Status = TicketAttendeeStatus.Valid,
    });
    harness.WithUnmatched(new TicketAttendee
    {
        Id = okId, VendorTicketId = "tkt_o", VendorEventId = "evt_active",
        AttendeeEmail = "o@x.com", Status = TicketAttendeeStatus.Valid,
    });
    harness.WithActiveYear(2026);
    harness.Provisioning.FindOrCreateUserByEmailAsync(
            "f@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
        .Throws(new InvalidOperationException("boom"));
    harness.Provisioning.FindOrCreateUserByEmailAsync(
            "o@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
        .Returns(new AccountProvisioningResult(new User { Id = Guid.NewGuid() }, true));

    var plan = new AttendeeImportPlan(
        new[]
        {
            new AttendeeImportDecision(failId, "f@x.com", "F", "tkt_f",
                AttendeeImportOutcome.CreateNewUser, null, null, null, null),
            new AttendeeImportDecision(okId, "o@x.com", "O", "tkt_o",
                AttendeeImportOutcome.CreateNewUser, null, null, null, null),
        }, 2);

    var result = await harness.Service.ApplyAsync(plan, new HashSet<Guid> { failId, okId });

    result.Errors.Should().Be(1);
    result.UsersCreated.Should().Be(1);
}
```

Add the matching using directive if not already present:
```csharp
using NSubstitute.ExceptionExtensions;
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServiceApplyTests"`
Expected: all 6 PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceApplyTests.cs
git commit -m "test: AttendeeContactImportService apply edge cases (selection, vanished, error isolation)"
```

---

## Task 13: Squatter-protection test (security property)

**Files:**
- Create: `tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceSquatterTests.cs`

- [ ] **Step 1: Write the squatter test**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeContactImportServiceSquatterTests
{
    [HumansFact]
    public async Task Squatter_UnverifiedRowDeletedBeforeNewUserCreated_NewUserNotAttachedToSquatter()
    {
        var harness = new ApplyHarness();
        var attendeeId = Guid.NewGuid();
        var squatterUserId = Guid.NewGuid();
        var squatterRowId = Guid.NewGuid();
        var newVictimUserId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId, VendorTicketId = "tkt_v", VendorEventId = "evt_active",
            AttendeeEmail = "victim@x.com", FirstName = "Victim",
            Status = TicketAttendeeStatus.Valid,
        };
        harness.WithUnmatched(attendee);
        harness.WithActiveYear(2026);
        harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>())
            .Returns(new AccountProvisioningResult(new User { Id = newVictimUserId }, Created: true));

        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(
                    attendeeId, "victim@x.com", "Victim", "tkt_v",
                    AttendeeImportOutcome.DeleteUnverifiedThenCreate,
                    null, squatterRowId, squatterUserId, null),
            }, 1);

        await harness.Service.ApplyAsync(plan, new HashSet<Guid> { attendeeId });

        // 1. Squatter row deleted.
        await harness.UserEmails.Received(1)
            .DeleteEmailAsync(squatterUserId, squatterRowId, Arg.Any<CancellationToken>());

        // 2. New user created — NOT attached to squatter.
        attendee.MatchedUserId.Should().Be(newVictimUserId);
        attendee.MatchedUserId.Should().NotBe(squatterUserId);

        // 3. Delete happened before create (Received.InOrder).
        Received.InOrder(() =>
        {
            harness.UserEmails.DeleteEmailAsync(squatterUserId, squatterRowId, Arg.Any<CancellationToken>());
            harness.Provisioning.FindOrCreateUserByEmailAsync(
                "victim@x.com", Arg.Any<string?>(), ContactSource.TicketTailor, Arg.Any<CancellationToken>());
        });
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportServiceSquatterTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/Tickets/AttendeeContactImportServiceSquatterTests.cs
git commit -m "test: squatter protection — unverified row deleted before victim user created"
```

---

## Task 14: Architecture tests

**Files:**
- Create: `tests/Humans.Application.Tests/Architecture/AttendeeContactImportArchitectureTests.cs`

- [ ] **Step 1: Write the architecture tests**

Mirror an existing arch-test in the same folder (`TicketSyncArchitectureTests.cs`) for the helper conventions — assertion style, ArchUnitNET vs reflection vs hand-rolled, etc. Adapt to:

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Xunit;

namespace Humans.Application.Tests.Architecture;

public class AttendeeContactImportArchitectureTests
{
    [HumansFact]
    public void Service_LivesInTicketsNamespace()
    {
        typeof(AttendeeContactImportService).Namespace
            .Should().Be("Humans.Application.Services.Tickets");
    }

    [HumansFact]
    public void Service_DoesNotReferenceDbContextOrEf()
    {
        var asmTypes = typeof(AttendeeContactImportService).Assembly.GetTypes();
        // Service must not import anything from Microsoft.EntityFrameworkCore or
        // reference HumansDbContext (Infrastructure type).
        var fields = typeof(AttendeeContactImportService).GetFields(
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

        foreach (var f in fields)
        {
            f.FieldType.FullName.Should().NotContain("Microsoft.EntityFrameworkCore");
            f.FieldType.FullName.Should().NotContain("HumansDbContext");
        }
    }

    [HumansFact]
    public void Service_DependsOnExpectedAbstractions()
    {
        var ctor = typeof(AttendeeContactImportService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        paramTypes.Should().Contain(typeof(ITicketRepository));
        paramTypes.Should().Contain(typeof(IUserEmailService));
        paramTypes.Should().Contain(typeof(IAccountProvisioningService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(IShiftManagementService));
        paramTypes.Should().Contain(typeof(ITicketQueryService));
        paramTypes.Should().Contain(typeof(IAuditLogService));
    }
}
```

> If the existing arch tests use a different style (e.g. `NetArchTest.Rules` predicate-builder, or a project-wide architecture-fitness helper), adopt that style instead. The assertions above describe the invariants in plain reflection — restructure to match.

- [ ] **Step 2: Run tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AttendeeContactImportArchitectureTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Architecture/AttendeeContactImportArchitectureTests.cs
git commit -m "test: architecture pins for AttendeeContactImportService"
```

---

## Task 15: DI registration

**Files:**
- Modify: `src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs`

- [ ] **Step 1: Register the service**

Open `src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs`. Find the existing `TicketSyncService` registration (look for `services.AddScoped<TicketsTicketSyncService>();`). Add nearby:

```csharp
services.AddScoped<IAttendeeContactImportService,
    Humans.Application.Services.Tickets.AttendeeContactImportService>();
```

(If the file already uses a `using` alias for `Humans.Application.Services.Tickets`, simplify accordingly.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs
git commit -m "feat: DI registration for IAttendeeContactImportService"
```

---

## Task 16: Controller + view-model — preview GET

**Files:**
- Create: `src/Humans.Web/Controllers/Tickets/TicketsContactsAdminController.cs`
- Create: `src/Humans.Web/Models/Tickets/ContactImportPreviewViewModel.cs`
- Create: `tests/Humans.Web.Tests/Controllers/Tickets/TicketsContactsAdminControllerTests.cs`

- [ ] **Step 1: Create the view-model**

```csharp
using Humans.Application.Interfaces.Tickets.Dtos;

namespace Humans.Web.Models.Tickets;

public sealed record ContactImportPreviewViewModel(
    AttendeeImportPlan Plan,
    IReadOnlyList<AttendeeImportDecisionRow> Rows);

public sealed record AttendeeImportDecisionRow(
    Guid AttendeeId,
    string? Email,
    string? AttendeeName,
    string VendorTicketId,
    AttendeeImportOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedRowUserId,
    IReadOnlyList<Guid>? AmbiguousUserIds);
```

- [ ] **Step 2: Write the failing controller test for GET**

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Web.Controllers.Tickets;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers.Tickets;

public class TicketsContactsAdminControllerTests
{
    [HumansFact]
    public async Task Index_RendersPreview_WithPlanAndProjectedRows()
    {
        var import = Substitute.For<IAttendeeContactImportService>();
        var attendeeId = Guid.NewGuid();
        var plan = new AttendeeImportPlan(
            new[]
            {
                new AttendeeImportDecision(attendeeId, "a@x.com", "A", "tkt_a",
                    AttendeeImportOutcome.CreateNewUser, null, null, null, null),
            }, 1);
        import.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);

        var controller = new TicketsContactsAdminController(
            import, Substitute.For<UserManager<User>>(
                Substitute.For<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!),
            NullLogger<TicketsContactsAdminController>.Instance);

        var result = await controller.Index(default);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        var vm = view.Model.Should().BeOfType<ContactImportPreviewViewModel>().Subject;
        vm.Plan.Should().BeSameAs(plan);
        vm.Rows.Should().ContainSingle()
            .Which.AttendeeId.Should().Be(attendeeId);
    }
}
```

> **Note:** the existing test layer may have a controller-test base class that handles `UserManager<User>` mocking. Look for `HumansControllerBase` or a similar test helper and use it if present.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketsContactsAdminControllerTests"`
Expected: FAIL — controller doesn't exist.

- [ ] **Step 4: Create the controller (GET only)**

```csharp
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Tickets.Dtos;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Controllers.Tickets;

[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Contacts")]
public sealed class TicketsContactsAdminController : HumansControllerBase
{
    private readonly IAttendeeContactImportService _import;
    private readonly ILogger<TicketsContactsAdminController> _logger;

    public TicketsContactsAdminController(
        IAttendeeContactImportService import,
        UserManager<User> userManager,
        ILogger<TicketsContactsAdminController> logger)
        : base(userManager)
    {
        _import = import;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var plan = await _import.BuildPlanAsync(ct);
        var rows = plan.Decisions
            .OrderBy(d => SortKey(d.Outcome))
            .ThenBy(d => d.Email)
            .Select(d => new AttendeeImportDecisionRow(
                d.AttendeeId, d.Email, d.AttendeeName, d.VendorTicketId,
                d.Outcome, d.TargetUserId, d.UnverifiedRowUserId, d.AmbiguousUserIds))
            .ToList();

        return View("~/Views/Tickets/Admin/Contacts.cshtml",
            new ContactImportPreviewViewModel(plan, rows));
    }

    // Ambiguous + DeleteUnverifiedThenCreate at top of the list so admin can't miss them.
    private static int SortKey(AttendeeImportOutcome o) => o switch
    {
        AttendeeImportOutcome.AmbiguousMultipleVerified => 0,
        AttendeeImportOutcome.DeleteUnverifiedThenCreate => 1,
        AttendeeImportOutcome.CreateNewUser => 2,
        AttendeeImportOutcome.AttachVerified => 3,
        AttendeeImportOutcome.SkipNoEmail => 4,
        AttendeeImportOutcome.SkipVoided => 5,
        _ => 99,
    };
}
```

> **Note:** verify `PolicyNames.TicketAdminOrAdmin` is the exact constant name — `grep -rn "TicketAdminOrAdmin" src/Humans.Web` will confirm. If the policy is named differently (e.g. `TicketAdmin_Or_Admin`), use the existing spelling.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketsContactsAdminControllerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/Tickets/TicketsContactsAdminController.cs \
        src/Humans.Web/Models/Tickets/ContactImportPreviewViewModel.cs \
        tests/Humans.Web.Tests/Controllers/Tickets/TicketsContactsAdminControllerTests.cs
git commit -m "feat: TicketsContactsAdminController GET preview"
```

---

## Task 17: Controller POST — apply selected

**Files:**
- Modify: `src/Humans.Web/Controllers/Tickets/TicketsContactsAdminController.cs`
- Modify: `tests/Humans.Web.Tests/Controllers/Tickets/TicketsContactsAdminControllerTests.cs`

- [ ] **Step 1: Add failing test**

Append to the existing test class:

```csharp
[HumansFact]
public async Task Apply_PassesSelectedIdsToService_AndRedirectsWithBanner()
{
    var import = Substitute.For<IAttendeeContactImportService>();
    var plan = new AttendeeImportPlan(Array.Empty<AttendeeImportDecision>(), 0);
    import.BuildPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);
    import.ApplyAsync(Arg.Any<AttendeeImportPlan>(),
            Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
        .Returns(new AttendeeImportResult(
            TotalAttempted: 1, UsersCreated: 1, AttachedToExistingVerified: 0,
            UnverifiedRowsDeletedAndUserCreated: 0, AmbiguousSkipped: 0, NoEmailSkipped: 0,
            VanishedBetweenPlanAndApply: 0, Errors: 0,
            Elapsed: NodaTime.Duration.FromSeconds(1)));

    var controller = new TicketsContactsAdminController(
        import, /* UserManager fake */ TestUserManagers.NoOp(),
        NullLogger<TicketsContactsAdminController>.Instance);
    controller.TempData = TestTempData.Empty();

    var selectedId = Guid.NewGuid();
    var result = await controller.Apply(new[] { selectedId }, default);

    result.Should().BeOfType<RedirectToActionResult>()
        .Which.ActionName.Should().Be(nameof(TicketsContactsAdminController.Index));
    controller.TempData["Banner"].Should().NotBeNull();

    await import.Received(1).ApplyAsync(
        plan,
        Arg.Is<IReadOnlySet<Guid>>(s => s.Contains(selectedId) && s.Count == 1),
        Arg.Any<CancellationToken>());
}

[HumansFact]
public async Task Apply_EmptySelection_RedirectsWithValidationBanner_NoServiceCall()
{
    var import = Substitute.For<IAttendeeContactImportService>();

    var controller = new TicketsContactsAdminController(
        import, TestUserManagers.NoOp(),
        NullLogger<TicketsContactsAdminController>.Instance);
    controller.TempData = TestTempData.Empty();

    var result = await controller.Apply(Array.Empty<Guid>(), default);

    result.Should().BeOfType<RedirectToActionResult>();
    controller.TempData["Banner"].Should().NotBeNull();
    await import.DidNotReceive().ApplyAsync(
        Arg.Any<AttendeeImportPlan>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>());
}
```

> Replace `TestUserManagers.NoOp()` and `TestTempData.Empty()` with whatever helpers exist in the test project for those concerns — these are the standard names; adjust to actual.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketsContactsAdminControllerTests"`
Expected: 2 new tests FAIL — `Apply` method doesn't exist.

- [ ] **Step 3: Implement Apply on the controller**

Add to `TicketsContactsAdminController`:

```csharp
[HttpPost("Apply")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Apply(
    [FromForm(Name = "selected")] Guid[] selected,
    CancellationToken ct)
{
    if (selected is null || selected.Length == 0)
    {
        TempData["Banner"] = "Select at least one attendee before applying.";
        return RedirectToAction(nameof(Index));
    }

    var fresh = await _import.BuildPlanAsync(ct);
    var result = await _import.ApplyAsync(fresh, new HashSet<Guid>(selected), ct);
    TempData["Banner"] = $"Attendee contact import: {result.FormatSummary()}";
    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketsContactsAdminControllerTests"`
Expected: 3 PASS total.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/Tickets/TicketsContactsAdminController.cs \
        tests/Humans.Web.Tests/Controllers/Tickets/TicketsContactsAdminControllerTests.cs
git commit -m "feat: TicketsContactsAdminController.Apply with selection guard"
```

---

## Task 18: Razor view — preview + apply

**Files:**
- Create: `src/Humans.Web/Views/Tickets/Admin/Contacts.cshtml`

- [ ] **Step 1: Inspect existing patterns**

Read `src/Humans.Web/Views/Mailer/Admin/Import.cshtml` for the preview-table style. Match its layout (Bootstrap 5 utility classes, count badges, form structure, anti-forgery token). The Mailer view is the reference.

- [ ] **Step 2: Create the view**

```cshtml
@using Humans.Application.Interfaces.Tickets.Dtos
@model Humans.Web.Models.Tickets.ContactImportPreviewViewModel
@{
    ViewData["Title"] = "Import Attendee Contacts";
    var c = Model.Plan.Counts;
}

<h1>Import Attendee Contacts</h1>

@if (TempData["Banner"] is string banner)
{
    <div class="alert alert-info" role="alert">@banner</div>
}

<p class="text-muted">
    Found @Model.Plan.TotalUnmatched unmatched attendee@(Model.Plan.TotalUnmatched == 1 ? "" : "s")
    for the active event. Each one below has no <code>MatchedUserId</code> yet.
    Select rows and click Apply.
</p>

<div class="mb-3">
    <span class="badge text-bg-success me-1">Attach existing: @c.AttachVerified</span>
    <span class="badge text-bg-primary me-1">Create new user: @c.CreateNewUser</span>
    <span class="badge text-bg-warning me-1">Delete unverified + create: @c.DeleteUnverifiedThenCreate</span>
    @if (c.AmbiguousMultipleVerified > 0)
    {
        <span class="badge text-bg-danger me-1">Ambiguous (skipped): @c.AmbiguousMultipleVerified</span>
    }
    @if (c.SkipNoEmail > 0)
    {
        <span class="badge text-bg-secondary me-1">No email (skipped): @c.SkipNoEmail</span>
    }
</div>

<form asp-action="Apply" method="post" id="contacts-apply">
    @Html.AntiForgeryToken()

    <div class="mb-2">
        <button type="button" class="btn btn-outline-secondary btn-sm" id="select-all">Select all</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" id="select-first">Select first 1</button>
        <button type="submit" class="btn btn-primary btn-sm" id="apply-btn">
            Apply selected (<span id="selected-count">0</span>)
        </button>
    </div>

    <table class="table table-sm table-hover">
        <thead>
            <tr>
                <th style="width:2rem"></th>
                <th>Email</th>
                <th>Name</th>
                <th>Decision</th>
                <th>Target / Notes</th>
                <th>Vendor Ticket Id</th>
            </tr>
        </thead>
        <tbody>
        @foreach (var row in Model.Rows)
        {
            <tr>
                <td>
                    @if (row.Outcome is AttendeeImportOutcome.AmbiguousMultipleVerified
                                      or AttendeeImportOutcome.SkipNoEmail
                                      or AttendeeImportOutcome.SkipVoided)
                    {
                        @* Non-actionable rows: no checkbox *@
                    }
                    else
                    {
                        <input type="checkbox" name="selected" value="@row.AttendeeId"
                               class="form-check-input row-check" />
                    }
                </td>
                <td>@(row.Email ?? "—")</td>
                <td>@(row.AttendeeName ?? "—")</td>
                <td>
                    @switch (row.Outcome)
                    {
                        case AttendeeImportOutcome.AttachVerified:
                            <span class="badge text-bg-success">Attach existing</span>
                            break;
                        case AttendeeImportOutcome.CreateNewUser:
                            <span class="badge text-bg-primary">Create new user</span>
                            break;
                        case AttendeeImportOutcome.DeleteUnverifiedThenCreate:
                            <span class="badge text-bg-warning">Delete unverified + create</span>
                            break;
                        case AttendeeImportOutcome.AmbiguousMultipleVerified:
                            <span class="badge text-bg-danger">Ambiguous</span>
                            break;
                        case AttendeeImportOutcome.SkipNoEmail:
                            <span class="badge text-bg-secondary">No email</span>
                            break;
                        case AttendeeImportOutcome.SkipVoided:
                            <span class="badge text-bg-secondary">Voided</span>
                            break;
                    }
                </td>
                <td>
                    @if (row.Outcome == AttendeeImportOutcome.AttachVerified && row.TargetUserId is Guid uid)
                    {
                        <a asp-controller="Admin" asp-action="EditUser" asp-route-id="@uid">
                            View user</a>
                    }
                    else if (row.Outcome == AttendeeImportOutcome.DeleteUnverifiedThenCreate
                             && row.UnverifiedRowUserId is Guid squatterId)
                    {
                        <text>Will delete unverified email row owned by </text>
                        <a asp-controller="Admin" asp-action="EditUser" asp-route-id="@squatterId">user</a>
                    }
                    else if (row.Outcome == AttendeeImportOutcome.AmbiguousMultipleVerified
                             && row.AmbiguousUserIds is { Count: > 0 } ids)
                    {
                        <text>Multiple verified owners: @string.Join(", ", ids)</text>
                    }
                </td>
                <td><code>@row.VendorTicketId</code></td>
            </tr>
        }
        </tbody>
    </table>
</form>

<script>
(function () {
    const checks = document.querySelectorAll('.row-check');
    const count = document.getElementById('selected-count');
    function refresh() {
        const n = Array.from(checks).filter(c => c.checked).length;
        count.textContent = n;
        document.getElementById('apply-btn').disabled = n === 0;
    }
    checks.forEach(c => c.addEventListener('change', refresh));
    document.getElementById('select-all').addEventListener('click', () => {
        checks.forEach(c => c.checked = true);
        refresh();
    });
    document.getElementById('select-first').addEventListener('click', () => {
        checks.forEach((c, i) => c.checked = (i === 0));
        refresh();
    });
    refresh();
})();
</script>
```

> **Asp action link verification:** the "View user" link assumes an admin user-edit controller exists at `Admin/EditUser?id=`. Run `grep -rn "EditUser\|UserEdit\|users/edit" src/Humans.Web/Controllers` to confirm the actual route. If the admin user controller uses a different name (e.g. `UsersAdminController.Edit`), update both `asp-controller` and `asp-action` accordingly.

- [ ] **Step 3: Build + smoke-render**

Run: `dotnet build Humans.slnx -v quiet`
Then start the app: `dotnet run --project src/Humans.Web`
Visit `https://localhost:7XXX/Tickets/Admin/Contacts` while signed in as a TicketAdmin/Admin user. Confirm the page renders (the unmatched list will probably be empty in dev unless the stub vendor produced unmatched attendees — that's fine, the empty-state should still render).

Stop the server when done.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Tickets/Admin/Contacts.cshtml
git commit -m "feat: Razor view for attendee contact import preview + apply"
```

---

## Task 19: Discoverability — link from the Tickets dashboard

**Files:**
- Modify: `src/Humans.Web/Views/Tickets/Index.cshtml` (or wherever the existing Sync/FullResync buttons live)

- [ ] **Step 1: Find the admin actions block**

Run: `grep -n "Sync\|FullResync\|FullReSync\|asp-action" src/Humans.Web/Views/Tickets/Index.cshtml | head -20`

Identify the block that renders the "Run Sync" / "Full Re-sync" action buttons.

- [ ] **Step 2: Add the link**

Inside that admin-actions block (gated on the same `@if (User.IsInRole(...))` or policy check the existing buttons use), add:

```cshtml
<a asp-controller="TicketsContactsAdmin" asp-action="Index"
   class="btn btn-outline-primary btn-sm">
    Import Attendee Contacts
</a>
```

- [ ] **Step 3: Smoke-test the link**

Start the app and confirm the new button appears for admin users on `/Tickets`, and clicking it navigates to `/Tickets/Admin/Contacts`.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Tickets/Index.cshtml
git commit -m "feat: dashboard link to attendee contacts import"
```

---

## Task 20: Section doc + freshness updates

**Files:**
- Modify: `docs/sections/tickets.md`
- Modify: `docs/architecture/freshness-catalog.yml` (if `tickets.md` has a catalog entry)

- [ ] **Step 1: Update `docs/sections/tickets.md`**

Add a paragraph to the **Concepts** section near the existing "Ticket Sync" description:

> **Attendee Contact Import** is a manually-triggered admin job (`IAttendeeContactImportService`) that creates a no-profile Humans user for each unmatched ticket attendee whose email doesn't already resolve to an existing UserEmail. Mirrors the Mailer import's plan/apply shape with squatter protection (unverified UserEmail rows are deleted before a fresh verified row is created for the new user). Decoupled from the sync today; Phase 2 will run it automatically at the end of each `TicketSyncService` run.

Add to the **Routing** table:

| `/Tickets/Admin/Contacts` | GET | `TicketAdminOrAdmin` | Preview attendee-contact-import plan |
| `/Tickets/Admin/Contacts/Apply` | POST | `TicketAdminOrAdmin` | Apply selected attendees |

Add to **Triggers**:

> - On attendee contact import apply: for selected unmatched attendees, `MatchedUserId` is set (via `UpsertAttendeesAsync`), new users are provisioned (via `IAccountProvisioningService` with `ContactSource.TicketTailor`, Stub Profile + verified `UserEmail`), squatter unverified rows are deleted first, `EventParticipation(Ticketed, TicketSync)` is written for each newly-matched user, ticket caches are invalidated via `ITicketQueryService.InvalidateAfterContactImport`, and a single `AuditAction.TicketContactsImported` row records the summary.

Extend the **Cross-Section Dependencies** list to mention `IAccountProvisioningService` (Users section).

Extend **Actors & Roles** — the "TicketAdmin, Admin" row gets a new capability: "Import attendee contacts (preview + selectively apply)".

Extend **Negative Access Rules** — add: "Board cannot trigger attendee contact import — same `TicketAdminOrAdmin` gate as the sync."

Update the freshness-trigger comment at the top of `tickets.md`:

```html
<!-- freshness:triggers
  ... (existing entries) ...
  src/Humans.Application/Services/Tickets/AttendeeContactImportService.cs
  src/Humans.Application/Interfaces/Tickets/IAttendeeContactImportService.cs
  src/Humans.Web/Controllers/Tickets/TicketsContactsAdminController.cs
-->
```

- [ ] **Step 2: Update freshness catalog (if entry exists)**

Run: `grep -A 10 "tickets.md" docs/architecture/freshness-catalog.yml`

If the catalog has a `tickets.md` entry with `triggers:`, add the three new file paths from Step 1 above to its trigger list. If the catalog auto-extracts from the markdown comment, the comment update above is sufficient.

- [ ] **Step 3: Commit**

```bash
git add docs/sections/tickets.md docs/architecture/freshness-catalog.yml
git commit -m "docs: section invariants + freshness for attendee contact import"
```

---

## Task 21: Full build + test pass, then push

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds with zero warnings introduced by these tasks (existing warnings are OK).

- [ ] **Step 2: Full test pass**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass. If pre-existing failures are present that are unrelated to this work, list them in the final commit message; otherwise expect a clean run.

- [ ] **Step 3: Push the branch**

Run: `git push origin feat/ticket-attendee-contact-import`

- [ ] **Step 4: Open the PR**

```bash
gh pr create --title "feat: attendee contact import (Tickets)" --body "$(cat <<'EOF'
## Summary
- New manually-triggered admin job that creates Humans users from unmatched ticket attendees, mirroring the Mailer plan/apply pattern.
- Squatter protection: unverified UserEmail rows are deleted before provisioning a new user with a verified row.
- Per-row checkbox preview at `/Tickets/Admin/Contacts` — admin can test against a single attendee before bulk-applying.
- Wires `EventParticipation(Ticketed, TicketSync)` immediately for newly-matched users (no wait for next sync).

Spec: `docs/superpowers/specs/2026-05-13-ticket-attendee-contact-import-design.md`
Plan: `docs/superpowers/plans/2026-05-13-ticket-attendee-contact-import.md`

## Test plan
- [ ] Build + test suite green
- [ ] Smoke: visit `/Tickets/Admin/Contacts` in dev, confirm preview renders
- [ ] Smoke: select 1 attendee, apply, confirm result banner + new user appears under `/Admin/Users`
- [ ] Smoke: select all, apply, confirm remaining attendees matched and `Volunteer Ticket Coverage` denominator on `/Tickets` updates

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 5: Confirm PR URL printed and visible**

Capture the PR URL printed by `gh pr create` and share it.

---

## Spec Coverage Check

| Spec section | Covered by task |
|---|---|
| New audit action `TicketContactsImported` | 1 |
| DTOs (Outcome, Decision, Plan, Result) | 2, 3, 4 |
| `IAttendeeContactImportService` interface | 5 |
| `GetUnmatchedActiveAttendeesAsync` repo method | 6 |
| `InvalidateAfterContactImport` cache seam | 7 |
| Plan classifier (six decisions) | 8, 9, 10 |
| Tombstone follow | 9 |
| `ApplyAsync` (selection, vanished, errors, audit, invalidate, participation) | 11, 12 |
| Squatter protection security property | 13 |
| Architecture tests | 14 |
| DI registration | 15 |
| Controller GET + POST with anti-forgery and policy | 16, 17 |
| Razor preview with checkbox UI, sort-Ambiguous-first, Select-all/Select-first-1 | 18 |
| Dashboard discoverability link | 19 |
| Section docs + freshness | 20 |
| Build + test + PR | 21 |
