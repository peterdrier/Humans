# Ticket Transfer UI Tweaks + Vendor Step History — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Polish the ticket-transfer admin/user surfaces (canonical `<vc:human>` rendering everywhere), capture structured vendor-call history for partial-failure recovery, fix the onward-transfer ownership bug, and add a retry-issue admin action.

**Architecture:** Domain owns one new property (`VendorStepsJson` on `TicketTransferRequest`) and one new static helper (`TicketAttendeeOwnership`). Application gets one new ownership rule, a step-log writeback inside `WriteToVendorAsync`, two new query-service methods (holdings, drift), and one new transfer-service action (retry-issue). Web gets two new view components (`<vc:ticket-holdings>`, `<vc:ticket-transfer-timeline>`), Index/Detail/Send view rewrites that swap rolled cards for `<vc:human>`, and a new admin retry-issue endpoint.

**Tech Stack:** ASP.NET Core MVC + Razor view components, EF Core (PostgreSQL via Npgsql), NodaTime `Instant`, NSubstitute + xUnit + AwesomeAssertions for tests.

**Working directory for all tasks:** `H:/source/Humans/.worktrees/ticket-transfer-tweaks` (already created off `origin/main` as branch `ticket-transfer-tweaks`).

**Spec:** [`docs/superpowers/specs/2026-05-12-ticket-transfer-ui-history-design.md`](../specs/2026-05-12-ticket-transfer-ui-history-design.md)

---

## Build sequence overview

- Phase A (Tasks 1–5): Domain helper + ownership cascade + vendor-step persistence
- Phase B (Tasks 6–8): New service-layer methods (holdings, drift, retry-issue)
- Phase C (Tasks 9–10): View components
- Phase D (Tasks 11–16): Page rewrites + controller action

Each task ends in a commit. Push at the end of each phase.

---

## Task 1: `TicketAttendeeOwnership` static helper

**Files:**
- Create: `src/Humans.Application/Services/Tickets/TicketAttendeeOwnership.cs`
- Test: `tests/Humans.Application.Tests/Services/Tickets/TicketAttendeeOwnershipTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Application.Tests/Services/Tickets/TicketAttendeeOwnershipTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class TicketAttendeeOwnershipTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    [Fact]
    public void CurrentOwner_PrefersAttendeeMatchedUserId_WhenSet()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().Be(UserB);
    }

    [Fact]
    public void CurrentOwner_FallsBackToOrderMatchedUserId_WhenAttendeeUnmatched()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = null,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().Be(UserA);
    }

    [Fact]
    public void CurrentOwner_ReturnsNull_WhenBothUnmatched()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = null,
            TicketOrder = new TicketOrder { MatchedUserId = null },
        };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().BeNull();
    }

    [Fact]
    public void CurrentOwner_ReturnsNull_WhenOrderNavigationMissing()
    {
        var attendee = new TicketAttendee { MatchedUserId = null, TicketOrder = null! };

        TicketAttendeeOwnership.CurrentOwner(attendee).Should().BeNull();
    }

    [Fact]
    public void IsCurrentOwner_True_WhenAttendeeMatched()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserB).Should().BeTrue();
        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserA).Should().BeFalse();
    }

    [Fact]
    public void IsCurrentOwner_True_ForOrderBuyer_WhenAttendeeUnmatched()
    {
        var attendee = new TicketAttendee
        {
            MatchedUserId = null,
            TicketOrder = new TicketOrder { MatchedUserId = UserA },
        };

        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserA).Should().BeTrue();
        TicketAttendeeOwnership.IsCurrentOwner(attendee, UserB).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests, confirm compile failure**

```
cd H:/source/Humans/.worktrees/ticket-transfer-tweaks
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketAttendeeOwnershipTests
```
Expected: build error — `TicketAttendeeOwnership` is undefined.

- [ ] **Step 3: Create the helper**

Create `src/Humans.Application/Services/Tickets/TicketAttendeeOwnership.cs`:

```csharp
using Humans.Domain.Entities;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Single source of truth for "who currently holds this issued ticket" — the
/// authority that can transfer it onward. Ownership cascades: the attendee's
/// matched user wins if set; otherwise we fall back to the order buyer.
/// Returns null when no Humans user holds it (vendor-only ticket).
/// </summary>
/// <remarks>
/// Cascade rationale: a buyer may purchase tickets for non-members (unmatched
/// attendees) and needs to manage those onward. Once a ticket gets matched to
/// a Humans user, that user takes ownership and the buyer can no longer
/// transfer it on their behalf. If sync later clears the attendee's match
/// (e.g. their email becomes unverified), the buyer regains transfer rights
/// — consistent fallback, not a special case.
/// </remarks>
public static class TicketAttendeeOwnership
{
    public static Guid? CurrentOwner(TicketAttendee attendee) =>
        attendee.MatchedUserId ?? attendee.TicketOrder?.MatchedUserId;

    public static bool IsCurrentOwner(TicketAttendee attendee, Guid userId) =>
        CurrentOwner(attendee) == userId;
}
```

- [ ] **Step 4: Run tests, confirm pass**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketAttendeeOwnershipTests
```
Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Tickets/TicketAttendeeOwnership.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketAttendeeOwnershipTests.cs
git commit -m "feat(tickets): add TicketAttendeeOwnership cascade helper"
```

---

## Task 2: Apply ownership cascade to `GetMyAttendeesAsync` + `CreateRequestAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/TicketTransferService.cs` (two call sites: `GetMyAttendeesAsync` line 123, `CreateRequestAsync` line 142)
- Test: `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_OnwardTransferTests.cs` (new)

- [ ] **Step 1: Write failing tests for onward-transfer authorisation**

Create `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_OnwardTransferTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
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

public sealed class TicketTransferService_OnwardTransferTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 12, 10, 0);
    private readonly FakeClock _clock = new(Now);

    private static readonly Guid UserA = Guid.NewGuid();   // original buyer
    private static readonly Guid UserB = Guid.NewGuid();   // post-transfer holder
    private static readonly Guid UserC = Guid.NewGuid();   // would-be next recipient
    private static readonly Guid OrderId = Guid.NewGuid();

    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferService_OnwardTransferTests()
    {
        _service = new TicketTransferService(_transferRepo, _ticketRepo, _vendor,
            _ticketQueryService, _userService, _userEmailService, _profileService,
            _auditLog, _clock, NullLogger<TicketTransferService>.Instance);
    }

    [Fact]
    public async Task GetMyAttendees_AllowsTransfer_WhenAttendeeMatchedToCaller_EvenIfBuyerIsSomeoneElse()
    {
        // Attendee row created post-A→B-transfer: matched to B, parent order belongs to A.
        var attendeeId = Guid.NewGuid();
        var attendee = new TicketAttendee
        {
            Id = attendeeId,
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserB, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        _transferRepo.GetBySenderAsync(UserB, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var rows = await _service.GetMyAttendeesAsync(UserB);

        rows.Should().HaveCount(1);
        rows[0].CanSendTransfer.Should().BeTrue();
    }

    [Fact]
    public async Task GetMyAttendees_DeniesTransfer_WhenAttendeeMatchedToSomeoneElse_EvenIfCallerIsBuyer()
    {
        var attendee = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = UserB,
            TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var rows = await _service.GetMyAttendeesAsync(UserA);

        rows.Should().HaveCount(1);
        rows[0].CanSendTransfer.Should().BeFalse();
    }

    [Fact]
    public async Task GetMyAttendees_AllowsBuyer_WhenAttendeeUnmatched()
    {
        var attendee = new TicketAttendee
        {
            Id = Guid.NewGuid(),
            Status = TicketAttendeeStatus.Valid,
            MatchedUserId = null,
            TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
        };
        _ticketRepo.GetAttendeesVisibleToUserAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(new[] { attendee });
        _transferRepo.GetBySenderAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        var rows = await _service.GetMyAttendeesAsync(UserA);

        rows[0].CanSendTransfer.Should().BeTrue();
    }

    [Fact]
    public async Task CreateRequest_RejectsBuyer_WhenAttendeeMatchedToDifferentUser()
    {
        var attendeeId = Guid.NewGuid();
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(new TicketAttendee
            {
                Id = attendeeId,
                Status = TicketAttendeeStatus.Valid,
                MatchedUserId = UserB,
                TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
            });

        // A is the parent-order buyer but B is the matched holder — A must not be allowed.
        var act = async () => await _service.CreateRequestAsync(
            new TicketTransferRequestDto(attendeeId, UserC, "test"), UserA);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*currently hold*");
    }

    [Fact]
    public async Task CreateRequest_AllowsAttendeeHolder_EvenIfBuyerIsSomeoneElse()
    {
        var attendeeId = Guid.NewGuid();
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
            .Returns(new TicketAttendee
            {
                Id = attendeeId,
                Status = TicketAttendeeStatus.Valid,
                MatchedUserId = UserB,
                TicketOrder = new TicketOrder { Id = OrderId, MatchedUserId = UserA },
            });
        _userService.GetByIdAsync(UserC, Arg.Any<CancellationToken>())
            .Returns(new User { Id = UserC, DisplayName = "Carol" });
        _userEmailService.GetPrimaryEmailAsync(UserC, Arg.Any<CancellationToken>())
            .Returns("carol@example.com");
        _transferRepo.GetBySenderAsync(UserB, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TicketTransferRequest>());

        // B (the current holder) initiates B→C. Should not throw.
        var result = await _service.CreateRequestAsync(
            new TicketTransferRequestDto(attendeeId, UserC, "passing to Carol"), UserB);

        result.SenderUserId.Should().Be(UserB);
        result.ReceiverUserId.Should().Be(UserC);
    }
}
```

- [ ] **Step 2: Run tests, confirm they fail**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService_OnwardTransferTests
```
Expected: 5 failed (3 fail because `CanSendTransfer` returns wrong value; 2 fail because `CreateRequestAsync` rejects/accepts on the wrong rule).

- [ ] **Step 3: Patch `TicketTransferService.GetMyAttendeesAsync`**

In `src/Humans.Application/Services/Tickets/TicketTransferService.cs`, locate the `CanSendTransfer` line (around 123):

```csharp
                    CanSendTransfer: a.Status == TicketAttendeeStatus.Valid
                        && a.TicketOrder.MatchedUserId == userId
                        && !pending,
```

Replace with:

```csharp
                    CanSendTransfer: a.Status == TicketAttendeeStatus.Valid
                        && TicketAttendeeOwnership.IsCurrentOwner(a, userId)
                        && !pending,
```

- [ ] **Step 4: Patch `TicketTransferService.CreateRequestAsync`**

Locate (around line 141):

```csharp
        // Sender must own the parent order's MatchedUserId
        if (attendee.TicketOrder.MatchedUserId != senderUserId)
            throw new InvalidOperationException("You can only transfer tickets from your own orders.");
```

Replace with:

```csharp
        // Sender must be the current holder of this attendee. Cascade rule
        // (TicketAttendeeOwnership): attendee.MatchedUserId wins; falls back
        // to the parent order's MatchedUserId if the attendee is unmatched.
        if (!TicketAttendeeOwnership.IsCurrentOwner(attendee, senderUserId))
            throw new InvalidOperationException("You can only transfer tickets you currently hold.");
```

- [ ] **Step 5: Run tests, confirm they pass and existing transfer tests still pass**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService
```
Expected: All `TicketTransferService*` tests pass. If any existing test in `TicketTransferServiceTests.cs` expects the old "your own orders" error string, update its assertion to "you currently hold".

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Tickets/TicketTransferService.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_OnwardTransferTests.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketTransferServiceTests.cs
git commit -m "fix(tickets): allow onward transfer via ownership cascade"
```

---

## Task 3: Migration — add `VendorStepsJson` column

**Files:**
- Modify: `src/Humans.Domain/Entities/TicketTransferRequest.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs`
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddTicketTransferVendorStepsJson.cs` (generated by EF)

- [ ] **Step 1: Add property to entity**

In `src/Humans.Domain/Entities/TicketTransferRequest.cs`, add after `AdminNotes` property (line 67):

```csharp
    /// <summary>
    /// JSON-serialised list of <c>TicketTransferVendorStep</c> capturing each
    /// sub-step of the vendor writeback (void, issue, local upsert, retry,
    /// manual reconcile). Empty list <c>"[]"</c> for transfers created before
    /// this feature shipped; null is never expected.
    /// </summary>
    public string VendorStepsJson { get; set; } = "[]";
```

- [ ] **Step 2: Add EF mapping**

In `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs`, add after the `AdminNotes` mapping (around line 50):

```csharp
        builder.Property(x => x.VendorStepsJson)
            .IsRequired()
            .HasDefaultValue("[]")
            .HasColumnType("text");
```

- [ ] **Step 3: Generate migration**

```
dotnet ef migrations add AddTicketTransferVendorStepsJson \
    --project src/Humans.Infrastructure \
    --startup-project src/Humans.Web \
    -v quiet
```

Inspect the generated migration; the `Up` should add a non-null `vendor_steps_json text` column with default `'[]'` on `ticket_transfer_requests`. If the column name or type differs, snake-case it in `HasColumnName("vendor_steps_json")` before regenerating.

- [ ] **Step 4: Build, verify migration applies cleanly**

```
dotnet build Humans.slnx -v quiet
```
Expected: clean build. (No application of migration here — that happens at next app startup.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Entities/TicketTransferRequest.cs \
        src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs \
        src/Humans.Infrastructure/Migrations/
git commit -m "feat(tickets): add VendorStepsJson column to ticket_transfer_requests"
```

---

## Task 4: `TicketTransferVendorStep` DTO + enum

**Files:**
- Create: `src/Humans.Application/DTOs/TicketTransferVendorStep.cs`

- [ ] **Step 1: Create the DTO and enum**

Create `src/Humans.Application/DTOs/TicketTransferVendorStep.cs`:

```csharp
using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>
/// Kinds of sub-step recorded inside <c>TicketTransferRequest.VendorStepsJson</c>.
/// </summary>
public enum TicketTransferVendorStepKind
{
    /// <summary>TT POST: void the original issued ticket (with hold).</summary>
    Void,

    /// <summary>TT POST: issue replacement against the reserved hold.</summary>
    Issue,

    /// <summary>Local DB upsert of the new + voided attendee rows.</summary>
    LocalWriteback,

    /// <summary>Retry of <see cref="Issue"/> by an admin after a partial failure.</summary>
    RetryIssue,

    /// <summary>Admin recorded that they reconciled the vendor side manually.</summary>
    ManualReconcile,
}

/// <summary>
/// One row in the structured vendor-step log. Captures what we asked the
/// vendor to do, what we got back, and what we then did locally. Append-only —
/// every change to the request appends a new step rather than mutating an
/// existing one. Surfaced via the transfer-review timeline.
/// </summary>
public sealed record TicketTransferVendorStep(
    TicketTransferVendorStepKind Kind,
    bool Success,
    Instant OccurredAt,
    string? VendorReferenceId,    // hold id on successful Void; new ticket id on successful Issue/RetryIssue
    string? RequestSummary,       // short; never the full request body
    string? ResponseSummary,      // short
    string? ErrorMessage);
```

- [ ] **Step 2: Build, confirm it compiles**

```
dotnet build Humans.slnx -v quiet
```
Expected: clean build.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/DTOs/TicketTransferVendorStep.cs
git commit -m "feat(tickets): add TicketTransferVendorStep DTO + enum"
```

---

## Task 5: Append vendor steps in `WriteToVendorAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/TicketTransferService.cs` (`WriteToVendorAsync` and a small helper)
- Test: `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_VendorStepsTests.cs` (new)

- [ ] **Step 1: Write failing tests**

Create `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_VendorStepsTests.cs`:

```csharp
using System.Text.Json;
using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class TicketTransferService_VendorStepsTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 12, 10, 0);
    private readonly FakeClock _clock = new(Now);
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid SenderId = Guid.NewGuid();
    private static readonly Guid ReceiverId = Guid.NewGuid();

    private readonly ITicketTransferRepository _transferRepo = Substitute.For<ITicketTransferRepository>();
    private readonly ITicketRepository _ticketRepo = Substitute.For<ITicketRepository>();
    private readonly ITicketVendorService _vendor = Substitute.For<ITicketVendorService>();
    private readonly ITicketQueryService _ticketQueryService = Substitute.For<ITicketQueryService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();

    private readonly TicketTransferService _service;

    public TicketTransferService_VendorStepsTests()
    {
        _service = new TicketTransferService(_transferRepo, _ticketRepo, _vendor,
            _ticketQueryService, _userService, _userEmailService, _profileService,
            _auditLog, _clock, NullLogger<TicketTransferService>.Instance);
        _userService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = SenderId, DisplayName = "Sender" });
    }

    private static TicketTransferRequest PendingRequest(Guid attendeeId) => new()
    {
        Id = Guid.NewGuid(),
        OriginalTicketAttendeeId = attendeeId,
        SenderUserId = SenderId,
        ReceiverUserId = ReceiverId,
        ReceiverLegalName = "Alice",
        ReceiverEmail = "alice@example.com",
        Status = TicketTransferStatus.Pending,
        VendorResult = TicketTransferVendorResult.NotAttempted,
        RequestedAt = Now,
        VendorStepsJson = "[]",
    };

    private static TicketAttendee ValidAttendee(Guid id) => new()
    {
        Id = id,
        VendorTicketId = "tt_orig",
        Status = TicketAttendeeStatus.Valid,
        VendorEventId = "ev_1",
        AttendeeName = "Original Holder",
        AttendeeEmail = "orig@example.com",
        TicketTypeName = "Standard",
        Price = 50m,
        TicketOrder = new TicketOrder { Id = Guid.NewGuid(), MatchedUserId = SenderId },
        TicketOrderId = Guid.NewGuid(),
    };

    private static IReadOnlyList<TicketTransferVendorStep> StepsOf(TicketTransferRequest r) =>
        JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(r.VendorStepsJson)!;

    [Fact]
    public async Task Approve_HappyPath_AppendsVoidIssueAndLocalWritebackSteps()
    {
        var attendeeId = Guid.NewGuid();
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync("tt_orig", true, Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tt_orig", "hold_123"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VendorTicketDto("tt_new", "ev_1", null, "Alice", "alice@example.com", "Standard", 50m, "valid"));

        await _service.ApproveAsync(request.Id, AdminId, null);

        var steps = StepsOf(request);
        steps.Should().HaveCount(3);
        steps[0].Kind.Should().Be(TicketTransferVendorStepKind.Void);
        steps[0].Success.Should().BeTrue();
        steps[0].VendorReferenceId.Should().Be("hold_123");
        steps[1].Kind.Should().Be(TicketTransferVendorStepKind.Issue);
        steps[1].Success.Should().BeTrue();
        steps[1].VendorReferenceId.Should().Be("tt_new");
        steps[2].Kind.Should().Be(TicketTransferVendorStepKind.LocalWriteback);
        steps[2].Success.Should().BeTrue();
    }

    [Fact]
    public async Task Approve_VoidFails_AppendsOnlyFailedVoidStep()
    {
        var attendeeId = Guid.NewGuid();
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("denied", TicketVendorFailureKind.Validation));

        await _service.ApproveAsync(request.Id, AdminId, null);

        var steps = StepsOf(request);
        steps.Should().HaveCount(1);
        steps[0].Kind.Should().Be(TicketTransferVendorStepKind.Void);
        steps[0].Success.Should().BeFalse();
        steps[0].ErrorMessage.Should().Contain("denied");
        request.VendorResult.Should().Be(TicketTransferVendorResult.Failed);
    }

    [Fact]
    public async Task Approve_IssueFails_AppendsSuccessfulVoidAndFailedIssue()
    {
        var attendeeId = Guid.NewGuid();
        var request = PendingRequest(attendeeId);
        _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(ValidAttendee(attendeeId));
        _vendor.VoidIssuedTicketAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new VoidIssuedTicketResult("tt_orig", "hold_abc"));
        _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TicketVendorWriteException("rate limited", TicketVendorFailureKind.RateLimited));

        await _service.ApproveAsync(request.Id, AdminId, null);

        var steps = StepsOf(request);
        steps.Should().HaveCount(2);
        steps[0].Kind.Should().Be(TicketTransferVendorStepKind.Void);
        steps[0].Success.Should().BeTrue();
        steps[0].VendorReferenceId.Should().Be("hold_abc");
        steps[1].Kind.Should().Be(TicketTransferVendorStepKind.Issue);
        steps[1].Success.Should().BeFalse();
        request.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
    }
}
```

- [ ] **Step 2: Run tests, confirm they fail**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService_VendorStepsTests
```
Expected: 3 failed (steps not appended yet).

- [ ] **Step 3: Add step-append helpers and call sites in `TicketTransferService`**

In `src/Humans.Application/Services/Tickets/TicketTransferService.cs`, add at top of file:

```csharp
using System.Text.Json;
```

Add private helpers near the bottom of the class (next to `BuildReceiverCardAsync`):

```csharp
    private static readonly JsonSerializerOptions VendorStepsJsonOptions = new(JsonSerializerDefaults.Web);

    private void AppendStep(TicketTransferRequest request, TicketTransferVendorStep step)
    {
        var list = JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(
            request.VendorStepsJson, VendorStepsJsonOptions) ?? new();
        list.Add(step);
        request.VendorStepsJson = JsonSerializer.Serialize(list, VendorStepsJsonOptions);
    }
```

Modify `WriteToVendorAsync`:

After the successful void call (the line `voidResult = await _vendor.VoidIssuedTicketAsync(...)`), append a step:

```csharp
        // Sub-step 1: void the original.
        VoidIssuedTicketResult voidResult;
        try
        {
            voidResult = await _vendor.VoidIssuedTicketAsync(
                attendee.VendorTicketId, voidToHold: true, ct);
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Void,
                Success: true,
                OccurredAt: _clock.GetCurrentInstant(),
                VendorReferenceId: voidResult.HoldId,
                RequestSummary: $"void vendorTicketId={attendee.VendorTicketId} voidToHold=true",
                ResponseSummary: voidResult.HoldId is null ? "void ok (no hold)" : $"void ok hold={voidResult.HoldId}",
                ErrorMessage: null));
        }
        catch (TicketVendorWriteException ex)
        {
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Void,
                Success: false,
                OccurredAt: _clock.GetCurrentInstant(),
                VendorReferenceId: null,
                RequestSummary: $"void vendorTicketId={attendee.VendorTicketId} voidToHold=true",
                ResponseSummary: null,
                ErrorMessage: $"({ex.Kind}) {ex.Message}"));
            request.VendorResult = TicketTransferVendorResult.Failed;
            request.VendorMessage = $"Void failed ({ex.Kind}): {ex.Message}";
            _logger.LogWarning(
                "TT void failed for transfer {TransferId} attendee {AttendeeId} ({Kind}); falling back to Option-C",
                request.Id, request.OriginalTicketAttendeeId, ex.Kind);
            return;
        }
```

Around the issue call:

```csharp
        // Sub-step 2: issue the replacement against the hold.
        VendorTicketDto issued;
        try
        {
            issued = await _vendor.IssueTicketAsync(new IssueTicketRequest(
                EventId: null,
                TicketTypeId: null,
                HoldId: voidResult.HoldId,
                FullName: request.ReceiverLegalName,
                Email: request.ReceiverEmail,
                SendEmail: true,
                ExternalReference: request.Id.ToString("N")), ct);
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Issue,
                Success: true,
                OccurredAt: _clock.GetCurrentInstant(),
                VendorReferenceId: issued.VendorTicketId,
                RequestSummary: $"issue hold={voidResult.HoldId} name={request.ReceiverLegalName}",
                ResponseSummary: $"issue ok ticket={issued.VendorTicketId}",
                ErrorMessage: null));
        }
        catch (TicketVendorWriteException ex)
        {
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Issue,
                Success: false,
                OccurredAt: _clock.GetCurrentInstant(),
                VendorReferenceId: null,
                RequestSummary: $"issue hold={voidResult.HoldId} name={request.ReceiverLegalName}",
                ResponseSummary: null,
                ErrorMessage: $"({ex.Kind}) {ex.Message}"));
            request.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
            request.VendorMessage = $"Issue failed ({ex.Kind}): {ex.Message} (hold {voidResult.HoldId})";
            _logger.LogError(ex,
                "TT issue failed for transfer {TransferId} after successful void; hold {HoldId} retained",
                request.Id, voidResult.HoldId);

            attendee.Status = TicketAttendeeStatus.Void;
            await _ticketRepo.UpsertAttendeeAsync(attendee, ct);
            _ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, receiverUserId: null);
            return;
        }
```

After the local upsert (`await _ticketRepo.UpsertAttendeesAsync(...)`):

```csharp
        try
        {
            await _ticketRepo.UpsertAttendeesAsync(new[] { /* …existing body unchanged… */ }, ct);
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.LocalWriteback,
                Success: true,
                OccurredAt: _clock.GetCurrentInstant(),
                VendorReferenceId: issued.VendorTicketId,
                RequestSummary: "upsert original→Void + new receiver row",
                ResponseSummary: "ok",
                ErrorMessage: null));
        }
        catch (Exception ex)
        {
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.LocalWriteback,
                Success: false,
                OccurredAt: _clock.GetCurrentInstant(),
                VendorReferenceId: null,
                RequestSummary: "upsert original→Void + new receiver row",
                ResponseSummary: null,
                ErrorMessage: ex.Message));
            throw;
        }
```

(Leave the original UpsertAttendeesAsync arguments unchanged — only wrap the call in try/catch for the LocalWriteback step. The "PARTIAL STATE" branch in `ApproveAsync` already handles the request-update failure separately.)

- [ ] **Step 4: Run tests, confirm they pass**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService_VendorStepsTests
```
Expected: 3 passed.

- [ ] **Step 5: Confirm existing tests still pass**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService
```
Expected: all pass.

- [ ] **Step 6: Commit + push phase A**

```bash
git add src/Humans.Application/Services/Tickets/TicketTransferService.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_VendorStepsTests.cs
git commit -m "feat(tickets): record structured vendor steps during transfer approval"
git push
```

---

## Task 6: `GetUserTicketHoldingsAsync` service method + cache

**Files:**
- Modify: `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs` (+method + DTO record)
- Modify: `src/Humans.Application/Services/Tickets/TicketQueryService.cs` (impl, cache key, invalidation)
- Test: `tests/Humans.Application.Tests/Services/Tickets/TicketQueryService_HoldingsTests.cs` (new)

- [ ] **Step 1: Add `UserTicketHoldings` record and method to the interface**

In `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs`, add (right after the existing `UserTicketOrderSummary` record at the bottom):

```csharp
/// <summary>
/// Holdings summary for a single user — orders they bought + attendee names
/// of the tickets they currently hold (per the ownership cascade). Used by
/// the &lt;vc:ticket-holdings&gt; profile sidebar and the transfer-review page.
/// </summary>
public record UserTicketHoldings(
    int OrderCount,
    IReadOnlyList<string> AttendeeNames);
```

In the interface body, add (anywhere before the closing brace, e.g. near `HasTicketAttendeeMatchAsync`):

```csharp
    /// <summary>
    /// Snapshot of a user's ticket holdings: count of orders where they're the
    /// buyer, plus the attendee names of every ticket where they are the
    /// current owner (per <c>TicketAttendeeOwnership</c>: matched attendee
    /// wins, falls back to order buyer for unmatched attendees).
    /// </summary>
    Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default);
```

- [ ] **Step 2: Write failing tests**

Create `tests/Humans.Application.Tests/Services/Tickets/TicketQueryService_HoldingsTests.cs`. Mirror the constructor wiring of existing `TicketQueryServiceTests.cs` (use same substitute set; adjust if that test class doesn't exist by following the `TicketTransferServiceTests` pattern). Skeleton:

```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Services.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class TicketQueryService_HoldingsTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    // … set up the same substitutes the existing TicketQueryServiceTests uses …

    [Fact]
    public async Task ReturnsEmpty_WhenUserHasNoOrdersAndNoAttendees()
    {
        // arrange: repo returns empty for both buyer-orders and visible attendees
        var result = await Service.GetUserTicketHoldingsAsync(UserA);
        result.OrderCount.Should().Be(0);
        result.AttendeeNames.Should().BeEmpty();
    }

    [Fact]
    public async Task CountsOrdersByBuyerAndAttendeeNamesByCurrentOwner()
    {
        // arrange:
        //  - UserA bought 2 orders
        //  - Order 1 has 2 attendees: one matched to UserA, one unmatched
        //  - Order 2 has 1 attendee matched to UserB (so it does NOT count for UserA's holdings)
        //  - Order 3 (someone else's order, MatchedUserId=UserB) has 1 attendee matched to UserA
        // Expected for UserA: 2 orders (the 2 they bought),
        //                     3 ticket names: own ticket, unmatched ticket (cascades to UserA), and the ticket matched to UserA on UserB's order

        // arrange the repo accordingly …

        var result = await Service.GetUserTicketHoldingsAsync(UserA);
        result.OrderCount.Should().Be(2);
        result.AttendeeNames.Should().HaveCount(3);
    }
}
```

(Engineer note: if `TicketQueryServiceTests.cs` already exists, mirror its constructor verbatim — same repo + cache + clock substitutes — to keep the wiring familiar. The two repository methods needed are "orders by buyer" and "attendees visible to user," both already on `ITicketRepository`.)

- [ ] **Step 3: Run tests, confirm they fail (compile failure on `GetUserTicketHoldingsAsync`)**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketQueryService_HoldingsTests
```
Expected: build failure.

- [ ] **Step 4: Implement `GetUserTicketHoldingsAsync` in `TicketQueryService`**

In `src/Humans.Application/Services/Tickets/TicketQueryService.cs`, add the method. Use `IMemoryCache` with key pattern matching the file's existing cache pattern (search for `CacheKeys.` to find the style; if missing, define a private const). Implementation:

```csharp
    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var cacheKey = $"UserTicketHoldings:{userId}";
        if (_cache.TryGetValue<UserTicketHoldings>(cacheKey, out var cached) && cached is not null)
            return cached;

        // Order count = orders where the user is the buyer.
        // Reuse the existing buyer-order projection to avoid a new repo method.
        var orderSummaries = await GetUserTicketOrderSummariesAsync(userId);
        var orderCount = orderSummaries.Count;

        // Attendee names = attendees where the current_owner == userId
        // (per TicketAttendeeOwnership: matched-to-user wins; falls back to
        // order buyer for unmatched attendees on this user's own orders).
        var attendees = await _ticketRepository.GetAttendeesVisibleToUserAsync(userId, ct);
        var names = attendees
            .Where(a => TicketAttendeeOwnership.IsCurrentOwner(a, userId))
            .OrderBy(a => a.AttendeeName, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.AttendeeName)
            .ToList();

        var holdings = new UserTicketHoldings(orderCount, names);
        _cache.Set(cacheKey, holdings, TimeSpan.FromMinutes(5));
        return holdings;
    }
```

Also extend the existing `InvalidateAfterTransfer` method to evict both parties' holdings:

```csharp
    public void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId)
    {
        // …existing cache evictions…
        _cache.Remove($"UserTicketHoldings:{senderUserId}");
        if (receiverUserId is { } rid)
            _cache.Remove($"UserTicketHoldings:{rid}");
    }
```

(If the existing `InvalidateAfterTransfer` already uses a centralised `CacheKeys.` helper, follow that pattern instead of inline strings.)

- [ ] **Step 5: Run tests, confirm pass**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketQueryService_HoldingsTests
```
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs \
        src/Humans.Application/Services/Tickets/TicketQueryService.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketQueryService_HoldingsTests.cs
git commit -m "feat(tickets): add GetUserTicketHoldingsAsync"
```

---

## Task 7: `GetOrderDriftAsync` service method

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs` (+method)
- Modify: `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` (+impl)
- Modify: `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs` (+method + DTO)
- Modify: `src/Humans.Application/Services/Tickets/TicketQueryService.cs` (+impl, simple pass-through)
- Test: `tests/Humans.Infrastructure.Tests/Repositories/Tickets/TicketRepository_OrderDriftTests.cs` (new — uses real EF in-memory or test fixture pattern; see existing repo tests for the harness)

- [ ] **Step 1: Add DTO + interface methods**

In `ITicketQueryService.cs`, add record + method:

```csharp
public sealed record OrderDriftRow(
    Guid OrderId,
    string VendorOrderId,
    string BuyerName,
    int IssuedCount,
    int ValidCount,
    string? VendorDashboardUrl);
```

```csharp
    /// <summary>
    /// Returns paid orders where the number of valid+checked-in attendees is
    /// less than the total number of attendees on the order. Catches "limbo"
    /// states from any cause (failed transfer reissue, manual TT-dashboard
    /// edits without resync, refunds, etc.).
    /// </summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);
```

In `ITicketRepository.cs`, add:

```csharp
    /// <summary>
    /// Returns paid orders whose live-ticket count (Valid or CheckedIn) is
    /// less than their total attendee count. Used by the admin order-drift
    /// diagnostic.
    /// </summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);
```

(Reuse `OrderDriftRow` from the `Humans.Application.Interfaces.Tickets` namespace by adding the using.)

- [ ] **Step 2: Implement in `TicketRepository`**

```csharp
    public async Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketOrders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .Select(o => new OrderDriftRow(
                o.Id,
                o.VendorOrderId,
                o.BuyerName,
                o.Attendees.Count(),
                o.Attendees.Count(a => a.Status == TicketAttendeeStatus.Valid
                                       || a.Status == TicketAttendeeStatus.CheckedIn),
                o.VendorDashboardUrl))
            .Where(r => r.ValidCount < r.IssuedCount)
            .OrderByDescending(r => r.IssuedCount - r.ValidCount)
            .ToListAsync(ct);
    }
```

- [ ] **Step 3: Implement in `TicketQueryService`**

```csharp
    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        _ticketRepository.GetOrderDriftAsync(ct);
```

(No caching — diagnostic page rarely viewed; freshness matters.)

- [ ] **Step 4: Write & run an integration-style test for the repo**

Mirror the pattern of an existing `*RepositoryTests` integration test (look for one in `tests/Humans.Infrastructure.Tests/Repositories/Tickets/` that uses a SQLite or Npgsql test fixture). The test:

```csharp
[Fact]
public async Task GetOrderDrift_ReturnsOrdersWhereValidLessThanIssued()
{
    // arrange:
    //  - "clean" order: 2 attendees, both Valid → not in result
    //  - "drift" order: 3 attendees, 2 Valid + 1 Void → in result with Issued=3 Valid=2
    //  - "refunded" order: 2 attendees, 1 Valid + 1 Void, PaymentStatus=Refunded → not in result
    // …seed the database fixture accordingly…

    var result = await _repo.GetOrderDriftAsync();

    result.Should().HaveCount(1);
    result[0].IssuedCount.Should().Be(3);
    result[0].ValidCount.Should().Be(2);
}
```

Run:

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketRepository_OrderDriftTests
```
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs \
        src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs \
        src/Humans.Application/Services/Tickets/TicketQueryService.cs \
        src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs \
        tests/Humans.Infrastructure.Tests/Repositories/Tickets/TicketRepository_OrderDriftTests.cs
git commit -m "feat(tickets): add GetOrderDriftAsync diagnostic query"
```

---

## Task 8: `RetryIssueAsync` service method

**Files:**
- Modify: `src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs`
- Modify: `src/Humans.Application/Services/Tickets/TicketTransferService.cs`
- Test: `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_RetryIssueTests.cs` (new)

- [ ] **Step 1: Add interface method**

In `ITicketTransferService.cs`:

```csharp
    /// <summary>
    /// Retry the issue half of a void+reissue that previously failed. Requires
    /// the request to be in state <c>Approved</c> with vendor result
    /// <c>VoidSucceededIssueFailed</c>; the hold id is read from the most
    /// recent successful <c>Void</c> step in <c>VendorStepsJson</c>. On
    /// success, inserts the new <c>TicketAttendee</c> row, sets
    /// <c>VendorResult=Succeeded</c>, appends a <c>RetryIssue</c> step, and
    /// audits + cache-invalidates. On failure, appends a failed
    /// <c>RetryIssue</c> step; state otherwise unchanged.
    /// </summary>
    Task<TicketTransferRowDto> RetryIssueAsync(
        Guid transferRequestId,
        Guid adminUserId,
        string? adminNotes,
        CancellationToken ct = default);
```

- [ ] **Step 2: Write failing tests**

Create `tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_RetryIssueTests.cs`. Wire the same substitute set as the other transfer service test files. Tests:

```csharp
[Fact]
public async Task RetryIssue_RejectsRequest_NotInPartialFailureState()
{
    var request = PendingRequest(Guid.NewGuid()); // Status=Pending
    _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);

    var act = async () => await _service.RetryIssueAsync(request.Id, AdminId, null);

    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*VoidSucceededIssueFailed*");
}

[Fact]
public async Task RetryIssue_RejectsRequest_WithNoRecordedHoldId()
{
    var request = new TicketTransferRequest
    {
        Id = Guid.NewGuid(),
        OriginalTicketAttendeeId = Guid.NewGuid(),
        SenderUserId = SenderId, ReceiverUserId = ReceiverId,
        ReceiverLegalName = "Alice", ReceiverEmail = "alice@example.com",
        Status = TicketTransferStatus.Approved,
        VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed,
        VendorStepsJson = "[]", // no Void step recorded
        RequestedAt = Now,
    };
    _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);

    var act = async () => await _service.RetryIssueAsync(request.Id, AdminId, null);

    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*hold*");
}

[Fact]
public async Task RetryIssue_HappyPath_InsertsAttendeeFlipsResultAppendsStep()
{
    var attendeeId = Guid.NewGuid();
    var origAttendee = ValidAttendee(attendeeId);
    origAttendee.Status = TicketAttendeeStatus.Void;

    var request = new TicketTransferRequest
    {
        Id = Guid.NewGuid(),
        OriginalTicketAttendeeId = attendeeId,
        SenderUserId = SenderId, ReceiverUserId = ReceiverId,
        ReceiverLegalName = "Alice", ReceiverEmail = "alice@example.com",
        Status = TicketTransferStatus.Approved,
        VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed,
        VendorStepsJson = """[{"Kind":"Void","Success":true,"OccurredAt":"2026-05-12T10:00:00Z","VendorReferenceId":"hold_abc","RequestSummary":null,"ResponseSummary":null,"ErrorMessage":null}]""",
        RequestedAt = Now,
    };
    _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
    _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>()).Returns(origAttendee);
    _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
        .Returns(new VendorTicketDto("tt_new2", "ev_1", null, "Alice", "alice@example.com", "Standard", 50m, "valid"));

    var row = await _service.RetryIssueAsync(request.Id, AdminId, "retrying");

    request.VendorResult.Should().Be(TicketTransferVendorResult.Succeeded);
    request.NewVendorTicketId.Should().Be("tt_new2");
    var steps = StepsOf(request);
    steps.Last().Kind.Should().Be(TicketTransferVendorStepKind.RetryIssue);
    steps.Last().Success.Should().BeTrue();
    await _ticketRepo.Received(1).UpsertAttendeeAsync(
        Arg.Is<TicketAttendee>(a => a.VendorTicketId == "tt_new2" && a.MatchedUserId == ReceiverId),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task RetryIssue_VendorFailure_AppendsFailedStepAndLeavesStateUnchanged()
{
    var attendeeId = Guid.NewGuid();
    var request = new TicketTransferRequest
    {
        Id = Guid.NewGuid(),
        OriginalTicketAttendeeId = attendeeId,
        SenderUserId = SenderId, ReceiverUserId = ReceiverId,
        ReceiverLegalName = "Alice", ReceiverEmail = "alice@example.com",
        Status = TicketTransferStatus.Approved,
        VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed,
        VendorStepsJson = """[{"Kind":"Void","Success":true,"OccurredAt":"2026-05-12T10:00:00Z","VendorReferenceId":"hold_abc","RequestSummary":null,"ResponseSummary":null,"ErrorMessage":null}]""",
        RequestedAt = Now,
    };
    _transferRepo.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
    _ticketRepo.GetAttendeeByIdAsync(attendeeId, Arg.Any<CancellationToken>())
        .Returns(ValidAttendee(attendeeId));
    _vendor.IssueTicketAsync(Arg.Any<IssueTicketRequest>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new TicketVendorWriteException("still rate limited", TicketVendorFailureKind.RateLimited));

    await _service.RetryIssueAsync(request.Id, AdminId, null);

    request.VendorResult.Should().Be(TicketTransferVendorResult.VoidSucceededIssueFailed);
    var steps = StepsOf(request);
    steps.Last().Kind.Should().Be(TicketTransferVendorStepKind.RetryIssue);
    steps.Last().Success.Should().BeFalse();
    await _ticketRepo.DidNotReceive().UpsertAttendeeAsync(Arg.Any<TicketAttendee>(), Arg.Any<CancellationToken>());
}
```

(Helpers `PendingRequest`, `ValidAttendee`, `StepsOf` mirror the ones in `TicketTransferService_VendorStepsTests`.)

- [ ] **Step 3: Run tests, confirm fail**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService_RetryIssueTests
```
Expected: 4 failed (method doesn't exist).

- [ ] **Step 4: Implement `RetryIssueAsync`**

In `TicketTransferService.cs`, add the method (next to `ApproveAsync`):

```csharp
    public async Task<TicketTransferRowDto> RetryIssueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await _transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Approved
            || request.VendorResult != TicketTransferVendorResult.VoidSucceededIssueFailed)
        {
            throw new InvalidOperationException(
                "Retry is only allowed when the transfer is Approved with VendorResult=VoidSucceededIssueFailed.");
        }

        var steps = JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(
            request.VendorStepsJson, VendorStepsJsonOptions) ?? new();
        var lastVoid = steps.LastOrDefault(s =>
            s.Kind == TicketTransferVendorStepKind.Void && s.Success && s.VendorReferenceId is not null);
        if (lastVoid is null)
            throw new InvalidOperationException("No recorded hold id to retry against.");

        var attendee = await _ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

        var now = _clock.GetCurrentInstant();
        VendorTicketDto issued;
        try
        {
            issued = await _vendor.IssueTicketAsync(new IssueTicketRequest(
                EventId: null, TicketTypeId: null, HoldId: lastVoid.VendorReferenceId,
                FullName: request.ReceiverLegalName, Email: request.ReceiverEmail,
                SendEmail: true, ExternalReference: request.Id.ToString("N")), ct);
        }
        catch (TicketVendorWriteException ex)
        {
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.RetryIssue,
                Success: false,
                OccurredAt: now,
                VendorReferenceId: null,
                RequestSummary: $"retry-issue hold={lastVoid.VendorReferenceId} name={request.ReceiverLegalName}",
                ResponseSummary: null,
                ErrorMessage: $"({ex.Kind}) {ex.Message}"));
            await _transferRepo.UpdateAsync(request, ct);
            await _auditLog.LogAsync(
                AuditAction.TicketTransferApproved,
                nameof(TicketTransferRequest), request.Id,
                $"Retry-issue failed: {ex.Message}",
                adminUserId, request.SenderUserId, nameof(User));
            return await BuildRowDtoAsync(request, ct);
        }

        // Issue succeeded — insert receiver row, flip result, audit.
        await _ticketRepo.UpsertAttendeeAsync(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = issued.VendorTicketId,
            TicketOrderId = attendee.TicketOrderId,
            AttendeeName = request.ReceiverLegalName,
            AttendeeEmail = request.ReceiverEmail,
            TicketTypeName = attendee.TicketTypeName,
            Price = attendee.Price,
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = attendee.VendorEventId,
            SyncedAt = now,
            MatchedUserId = request.ReceiverUserId,
        }, ct);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = $"hold {lastVoid.VendorReferenceId} (retry)";
        if (!string.IsNullOrWhiteSpace(adminNotes))
            request.AdminNotes = string.IsNullOrEmpty(request.AdminNotes)
                ? adminNotes
                : request.AdminNotes + "\nretry: " + adminNotes;

        AppendStep(request, new TicketTransferVendorStep(
            Kind: TicketTransferVendorStepKind.RetryIssue,
            Success: true,
            OccurredAt: now,
            VendorReferenceId: issued.VendorTicketId,
            RequestSummary: $"retry-issue hold={lastVoid.VendorReferenceId} name={request.ReceiverLegalName}",
            ResponseSummary: $"issue ok ticket={issued.VendorTicketId}",
            ErrorMessage: null));

        await _transferRepo.UpdateAsync(request, ct);

        _ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);

        await _auditLog.LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest), request.Id,
            $"Retry-issue success: ticket {issued.VendorTicketId}",
            adminUserId, request.SenderUserId, nameof(User));

        return await BuildRowDtoAsync(request, ct);
    }
```

- [ ] **Step 5: Run tests, confirm pass**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService_RetryIssueTests
```
Expected: 4 passed.

- [ ] **Step 6: Commit + push phase B**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs \
        src/Humans.Application/Services/Tickets/TicketTransferService.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketTransferService_RetryIssueTests.cs
git commit -m "feat(tickets): add RetryIssueAsync admin action"
git push
```

---

## Task 9: `<vc:ticket-holdings>` view component

**Files:**
- Create: `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs`
- Create: `src/Humans.Web/Views/Shared/Components/TicketHoldings/Default.cshtml`

- [ ] **Step 1: Create the component class**

Create `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs`:

```csharp
using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent : ViewComponent
{
    private readonly ITicketQueryService _queryService;

    public TicketHoldingsViewComponent(ITicketQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool showEmpty = false)
    {
        var holdings = await _queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.AttendeeNames.Count == 0)
            return Content(string.Empty);

        return View(new TicketHoldingsViewModel(holdings.OrderCount, holdings.AttendeeNames));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<string> AttendeeNames);
```

- [ ] **Step 2: Create the view**

Create `src/Humans.Web/Views/Shared/Components/TicketHoldings/Default.cshtml`:

```cshtml
@model Humans.Web.ViewComponents.TicketHoldingsViewModel

<div class="card mb-3">
    <div class="card-header py-2">
        <h3 class="h6 mb-0">Tickets</h3>
    </div>
    <div class="card-body py-2 small">
        <div class="d-flex align-items-center mb-1">
            <i class="fa-solid fa-receipt me-2 text-muted"></i>
            <span>@Model.OrderCount @(Model.OrderCount == 1 ? "order" : "orders")</span>
        </div>
        <div class="d-flex align-items-start">
            <i class="fa-solid fa-ticket me-2 mt-1 text-muted"></i>
            <div>
                <div>@Model.AttendeeNames.Count @(Model.AttendeeNames.Count == 1 ? "ticket" : "tickets")</div>
                @if (Model.AttendeeNames.Count > 0)
                {
                    <div class="text-muted">@string.Join(", ", Model.AttendeeNames)</div>
                }
            </div>
        </div>
    </div>
</div>
```

- [ ] **Step 3: Smoke-test in the running app**

```
dotnet run --project src/Humans.Web
```
Open `https://localhost:5001/Tickets/Admin/Transfers/Detail/<some-pending-transfer-id>` (will need a real transfer; if none, defer the smoke until Task 12 where holdings appears on the Detail page).

Confirm no compile errors at startup.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs \
        src/Humans.Web/Views/Shared/Components/TicketHoldings/Default.cshtml
git commit -m "feat(tickets): add <vc:ticket-holdings> view component"
```

---

## Task 10: `<vc:ticket-transfer-timeline>` view component

**Files:**
- Create: `src/Humans.Web/ViewComponents/TicketTransferTimelineViewComponent.cs`
- Create: `src/Humans.Web/Views/Shared/Components/TicketTransferTimeline/Default.cshtml`

- [ ] **Step 1: Create the component class**

Create `src/Humans.Web/ViewComponents/TicketTransferTimelineViewComponent.cs`:

```csharp
using System.Text.Json;
using Humans.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketTransferTimelineViewComponent : ViewComponent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IViewComponentResult Invoke(TicketTransferRowDto request, string vendorStepsJson)
    {
        var steps = JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(
            vendorStepsJson, JsonOptions) ?? new();
        return View(new TicketTransferTimelineViewModel(request, steps));
    }
}

public sealed record TicketTransferTimelineViewModel(
    TicketTransferRowDto Request,
    IReadOnlyList<TicketTransferVendorStep> Steps);
```

(Pass `vendorStepsJson` explicitly rather than letting the component pull from the request — keeps the DTO clean and avoids leaking persistence-shaped JSON into the row DTO. The Detail view passes both.)

- [ ] **Step 2: Create the view**

Create `src/Humans.Web/Views/Shared/Components/TicketTransferTimeline/Default.cshtml`:

```cshtml
@using Humans.Application.DTOs
@using Humans.Domain.Enums
@model Humans.Web.ViewComponents.TicketTransferTimelineViewModel

<div class="card mb-3">
    <div class="card-header py-2"><h3 class="h6 mb-0">Vendor step history</h3></div>
    <div class="card-body py-2 small">
        <div class="mb-2">
            <strong>Requested</strong>
            <span class="text-muted">@Model.Request.RequestedAt.ToDisplayDateTime()</span>
            — @Model.Request.SenderDisplayName
            @if (!string.IsNullOrWhiteSpace(Model.Request.SenderReason))
            {
                <span class="text-muted">— "@Model.Request.SenderReason"</span>
            }
        </div>

        @if (Model.Request.DecidedAt is not null)
        {
            var statusLabel = Model.Request.Status switch
            {
                TicketTransferStatus.Approved => "Approved",
                TicketTransferStatus.Rejected => "Rejected",
                TicketTransferStatus.Cancelled => "Cancelled",
                _ => Model.Request.Status.ToString(),
            };
            <div class="mb-2">
                <strong>@statusLabel</strong>
                <span class="text-muted">@Model.Request.DecidedAt!.Value.ToDisplayDateTime()</span>
                @if (!string.IsNullOrEmpty(Model.Request.DecidedByDisplayName))
                {
                    <text> — @Model.Request.DecidedByDisplayName</text>
                }
                @if (!string.IsNullOrEmpty(Model.Request.AdminNotes))
                {
                    <div class="text-muted">@Model.Request.AdminNotes</div>
                }
            </div>
        }

        @if (Model.Steps.Count == 0)
        {
            <p class="text-muted mb-0">No vendor step detail recorded (transfer pre-dates step-log feature).</p>
        }
        else
        {
            <ul class="list-unstyled mb-0">
                @foreach (var step in Model.Steps)
                {
                    var icon = step.Success ? "fa-circle-check text-success" : "fa-circle-xmark text-danger";
                    <li class="mb-1">
                        <i class="fa-solid @icon me-1"></i>
                        <strong>@step.Kind</strong>
                        <span class="text-muted">@step.OccurredAt.ToDisplayDateTime()</span>
                        @if (!string.IsNullOrEmpty(step.VendorReferenceId))
                        {
                            <code class="ms-1">@step.VendorReferenceId</code>
                        }
                        @if (!string.IsNullOrEmpty(step.ResponseSummary))
                        {
                            <span class="text-muted ms-1">— @step.ResponseSummary</span>
                        }
                        @if (!string.IsNullOrEmpty(step.ErrorMessage))
                        {
                            <div class="text-danger ms-4 small">@step.ErrorMessage</div>
                        }
                    </li>
                }
            </ul>
        }
    </div>
</div>
```

- [ ] **Step 3: Build, confirm compile**

```
dotnet build Humans.slnx -v quiet
```
Expected: clean build.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/ViewComponents/TicketTransferTimelineViewComponent.cs \
        src/Humans.Web/Views/Shared/Components/TicketTransferTimeline/Default.cshtml
git commit -m "feat(tickets): add <vc:ticket-transfer-timeline> view component"
```

---

## Task 11: Extend `TicketTransferDetailDto` + `GetDetailAsync` with `OrderDashboardUrl` and `VendorStepsJson`

**Files:**
- Modify: `src/Humans.Application/DTOs/TicketTransferDtos.cs`
- Modify: `src/Humans.Application/Services/Tickets/TicketTransferService.cs` (`GetDetailAsync`)

- [ ] **Step 1: Extend the DTO**

In `TicketTransferDtos.cs`, replace the `TicketTransferDetailDto` record with:

```csharp
public sealed record TicketTransferDetailDto(
    TicketTransferRowDto Row,
    ReceiverLookupResultDto SenderCard,
    ReceiverLookupResultDto ReceiverCard,
    string? OrderDashboardUrl,
    string VendorStepsJson);
```

- [ ] **Step 2: Populate the new fields in `GetDetailAsync`**

In `TicketTransferService.GetDetailAsync`, replace the existing return statement with:

```csharp
        var attendee = await _ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct);

        return new TicketTransferDetailDto(
            Row: row,
            SenderCard: senderCard ?? StubCard(request.SenderUserId, row.SenderDisplayName),
            ReceiverCard: receiverCard ?? StubCard(request.ReceiverUserId, row.ReceiverLegalName),
            OrderDashboardUrl: attendee?.TicketOrder?.VendorDashboardUrl,
            VendorStepsJson: request.VendorStepsJson);
```

(Confirm `GetAttendeeByIdAsync` returns the attendee with its `TicketOrder` navigation included; if not, follow the existing eager-load convention in the repository — likely an `.Include(a => a.TicketOrder)`.)

- [ ] **Step 3: Update existing `GetDetailAsync` tests for the new DTO shape**

In `tests/Humans.Application.Tests/Services/Tickets/TicketTransferServiceTests.cs`, find any assertion that constructs/destructures `TicketTransferDetailDto` and add the two new fields (use `null` and `"[]"` defaults where appropriate).

- [ ] **Step 4: Build + test**

```
dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~TicketTransferService
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/DTOs/TicketTransferDtos.cs \
        src/Humans.Application/Services/Tickets/TicketTransferService.cs \
        tests/Humans.Application.Tests/Services/Tickets/TicketTransferServiceTests.cs
git commit -m "feat(tickets): expose OrderDashboardUrl + VendorStepsJson on TransferDetailDto"
```

---

## Task 12: Rewrite Detail page — `<vc:human>` cards, timeline, audit embed, TT deep-link, retry form

**Files:**
- Modify: `src/Humans.Web/Views/TicketTransferAdmin/Detail.cshtml` (full rewrite)

- [ ] **Step 1: Rewrite the view**

Replace the entire contents of `src/Humans.Web/Views/TicketTransferAdmin/Detail.cshtml` with:

```cshtml
@using Humans.Domain.Enums
@model Humans.Application.DTOs.TicketTransferDetailDto
@{
    ViewData["Title"] = "Review transfer";
    var row = Model.Row;
    var (statusLabel, statusClass) = row.OriginalAttendeeStatus switch
    {
        TicketAttendeeStatus.Valid => ("Valid (not checked in)", "bg-success"),
        TicketAttendeeStatus.CheckedIn => ("Checked in", "bg-warning text-dark"),
        TicketAttendeeStatus.Void => ("Void", "bg-secondary"),
        _ => (row.OriginalAttendeeStatus.ToString(), "bg-secondary"),
    };
}

<h1>Review transfer</h1>

<vc:temp-data-alerts />

<div class="card mb-3">
    <div class="card-body">
        <h2 class="h5 mb-2">Ticket</h2>
        <div>@row.OriginalAttendeeName — @row.TicketTypeName</div>
        <div class="mt-2 d-flex align-items-center gap-2">
            <span class="badge @statusClass">@statusLabel</span>
            @if (!string.IsNullOrEmpty(Model.OrderDashboardUrl))
            {
                <a href="@Model.OrderDashboardUrl" target="_blank" rel="noopener"
                   class="btn btn-sm btn-outline-secondary">
                    View order in TicketTailor
                    <i class="fa-solid fa-arrow-up-right-from-square ms-1"></i>
                </a>
            }
        </div>
        @if (row.OriginalAttendeeStatus == TicketAttendeeStatus.CheckedIn)
        {
            <div class="alert alert-warning mt-2 mb-0">
                This ticket has already been checked in. Approving will void it at the vendor
                and reissue to the Receiver — confirm with the Sender before proceeding.
            </div>
        }
    </div>
</div>

<div class="row g-3 mb-3">
    <div class="col-md-6">
        <h3 class="h6 text-uppercase text-muted">Sender</h3>
        <vc:human user-id="@row.SenderUserId" layout="Card" link="Admin" />
        <vc:ticket-holdings user-id="@row.SenderUserId" show-empty="true" />
    </div>
    <div class="col-md-6">
        <h3 class="h6 text-uppercase text-muted">Recipient</h3>
        <vc:human user-id="@row.ReceiverUserId" layout="Card" link="Admin" />
    </div>
</div>

<dl class="row">
    <dt class="col-sm-3">Reason</dt><dd class="col-sm-9">@row.SenderReason</dd>
    <dt class="col-sm-3">Requested</dt><dd class="col-sm-9">@row.RequestedAt.ToDisplayDateTime()</dd>
</dl>

@if (row.Status == TicketTransferStatus.Pending)
{
    <form asp-action="Decide" method="post" class="mt-3">
        @Html.AntiForgeryToken()
        <input type="hidden" name="id" value="@row.Id" />
        <div class="mb-3">
            <label for="adminNotes">Admin notes (optional)</label>
            <textarea id="adminNotes" name="adminNotes" rows="2" class="form-control" maxlength="1000"></textarea>
        </div>
        <button type="submit" name="approve" value="true" class="btn btn-success">Approve</button>
        <button type="submit" name="approve" value="false" class="btn btn-danger">Reject</button>
        <a class="btn btn-outline-secondary" asp-action="Index">Back</a>
    </form>
}
else
{
    <a class="btn btn-outline-secondary mt-2" asp-action="Index">Back</a>
}

@if (row.Status == TicketTransferStatus.Approved
     && row.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed)
{
    <form asp-action="RetryIssue" asp-route-id="@row.Id" method="post" class="mt-3">
        @Html.AntiForgeryToken()
        <div class="alert alert-warning mb-2">
            Void succeeded but reissue failed. The hold is still reserved at the vendor.
            Retry will issue against the existing hold.
        </div>
        <div class="mb-2">
            <label for="retryNotes">Notes (optional)</label>
            <textarea id="retryNotes" name="adminNotes" rows="2" class="form-control" maxlength="1000"></textarea>
        </div>
        <button type="submit" class="btn btn-warning">Retry issue</button>
    </form>
}

<vc:ticket-transfer-timeline request="@Model.Row" vendor-steps-json="@Model.VendorStepsJson" />

<vc:audit-log entity-type="TicketTransferRequest" entity-id="@row.Id" title="Request audit history" limit="20" />
```

- [ ] **Step 2: Build + smoke-test**

```
dotnet build Humans.slnx -v quiet
```
Expected: clean build.

Run the app and navigate to a real `/Tickets/Admin/Transfers/Detail/{id}` page. Verify:
- Both Sender and Receiver cards render via `<vc:human>` (avatar + name).
- Holdings card renders under Sender.
- "View order in TicketTailor" button appears if the parent order has a `VendorDashboardUrl`.
- Timeline renders below the form. If the transfer is pre-step-log, it shows "No vendor step detail recorded".
- Audit log embed renders below the timeline.
- Retry-issue form appears only for Approved+VoidSucceededIssueFailed.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/TicketTransferAdmin/Detail.cshtml
git commit -m "feat(tickets): rewrite transfer Detail page with canonical components"
```

---

## Task 13: `RetryIssue` controller action

**Files:**
- Modify: `src/Humans.Web/Controllers/TicketTransferAdminController.cs`

- [ ] **Step 1: Add the action**

In `TicketTransferAdminController.cs`, add after `Decide`:

```csharp
    [HttpPost("{id:guid}/RetryIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryIssue(
        Guid id, string? adminNotes, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var result = await _service.RetryIssueAsync(id, user.Id, adminNotes, ct);
            if (result.VendorResult == TicketTransferVendorResult.Succeeded)
            {
                SetSuccess($"Retry succeeded — new ticket {result.VendorMessage ?? result.Id.ToString()}.");
            }
            else
            {
                SetError($"Retry failed: {result.VendorMessage}.");
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Retry-issue rejected for transfer {TransferId}: {Message}",
                id, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Detail), new { id });
    }
```

(Add `using Humans.Domain.Enums;` if not already imported — needed for `TicketTransferVendorResult`.)

- [ ] **Step 2: Build, smoke-test in app**

```
dotnet build Humans.slnx -v quiet
```

In the running app, force a partial vendor failure (or pick a test transfer in that state) → click "Retry issue" → confirm redirect back to Detail with toast.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/TicketTransferAdminController.cs
git commit -m "feat(tickets): add /Tickets/Admin/Transfers/{id}/RetryIssue action"
```

---

## Task 14: Rewrite Index page — tabs + columns + drift card

**Files:**
- Modify: `src/Humans.Web/Controllers/TicketTransferAdminController.cs` (`Index` action — accept `tab` param, return all states + drift)
- Modify: `src/Humans.Web/Views/TicketTransferAdmin/Index.cshtml` (full rewrite)
- Modify: `src/Humans.Web/Models/TicketTransferViewModels.cs` (new view model for Index)

- [ ] **Step 1: Add an Index view model**

In `src/Humans.Web/Models/TicketTransferViewModels.cs`, add:

```csharp
public sealed record TicketTransferIndexViewModel(
    string ActiveTab,
    int PendingCount,
    int NeedsAttentionCount,
    IReadOnlyList<TicketTransferRowDto> Rows,
    IReadOnlyList<OrderDriftRow> Drift);
```

(Use `Humans.Application.DTOs.TicketTransferRowDto` and `Humans.Application.Interfaces.Tickets.OrderDriftRow` namespaces.)

- [ ] **Step 2: Rewrite the Index action**

Replace the existing `Index` method in `TicketTransferAdminController.cs` with:

```csharp
    [HttpGet("")]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct)
    {
        tab ??= "pending";

        var pendingAll = await _service.GetByStatusAsync(TicketTransferStatus.Pending, ct);
        var pending = pendingAll.OrderBy(r => r.RequestedAt).ToList(); // FIFO

        // "Needs attention": approved transfers whose vendor writeback ended in
        // a state the admin must clean up (failed void / failed reissue).
        var approvedAll = await _service.GetByStatusAsync(TicketTransferStatus.Approved, ct);
        var needsAttention = approvedAll
            .Where(r => r.VendorResult == TicketTransferVendorResult.Failed
                     || r.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed)
            .OrderByDescending(r => r.DecidedAt)
            .ToList();

        IReadOnlyList<TicketTransferRowDto> rows = tab switch
        {
            "needs-attention" => needsAttention,
            "all" => await BuildAllAsync(ct),
            _ => pending,
        };

        var drift = await _ticketQueryService.GetOrderDriftAsync(ct);

        return View(new TicketTransferIndexViewModel(
            ActiveTab: tab,
            PendingCount: pending.Count,
            NeedsAttentionCount: needsAttention.Count,
            Rows: rows,
            Drift: drift));
    }

    private async Task<IReadOnlyList<TicketTransferRowDto>> BuildAllAsync(CancellationToken ct)
    {
        var statuses = Enum.GetValues<TicketTransferStatus>();
        var combined = new List<TicketTransferRowDto>();
        foreach (var s in statuses)
            combined.AddRange(await _service.GetByStatusAsync(s, ct));
        return combined.OrderByDescending(r => r.RequestedAt).ToList();
    }
```

Inject `ITicketQueryService _ticketQueryService` into the controller (add to constructor + field).

- [ ] **Step 3: Rewrite the view**

Replace the contents of `src/Humans.Web/Views/TicketTransferAdmin/Index.cshtml` with:

```cshtml
@model Humans.Web.Models.TicketTransferIndexViewModel
@{
    ViewData["Title"] = "Ticket transfer requests";
    string TabUrl(string t) => Url.Action(nameof(Humans.Web.Controllers.TicketTransferAdminController.Index), new { tab = t })!;
    string ActiveCls(string t) => Model.ActiveTab == t ? "active" : "";
}

<h1>Ticket transfer requests</h1>

<vc:temp-data-alerts />

@if (Model.Drift.Count > 0)
{
    <div class="alert alert-warning d-flex justify-content-between align-items-start">
        <div>
            <strong>⚠ Order drift detected.</strong>
            @Model.Drift.Count paid order(s) have fewer valid tickets than originally issued.
            <details class="mt-2">
                <summary>Show orders</summary>
                <ul class="mb-0 mt-1">
                    @foreach (var d in Model.Drift)
                    {
                        <li>
                            @d.BuyerName — @d.VendorOrderId
                            <span class="text-muted">(@d.ValidCount of @d.IssuedCount valid)</span>
                            @if (!string.IsNullOrEmpty(d.VendorDashboardUrl))
                            {
                                <a href="@d.VendorDashboardUrl" target="_blank" rel="noopener">View in TT</a>
                            }
                        </li>
                    }
                </ul>
            </details>
        </div>
    </div>
}

<ul class="nav nav-tabs mb-3">
    <li class="nav-item">
        <a class="nav-link @ActiveCls("pending")" href="@TabUrl("pending")">
            Pending <span class="badge bg-secondary">@Model.PendingCount</span>
        </a>
    </li>
    <li class="nav-item">
        <a class="nav-link @ActiveCls("needs-attention")" href="@TabUrl("needs-attention")">
            Needs attention
            @if (Model.NeedsAttentionCount > 0)
            {
                <span class="badge bg-warning text-dark">@Model.NeedsAttentionCount</span>
            }
        </a>
    </li>
    <li class="nav-item">
        <a class="nav-link @ActiveCls("all")" href="@TabUrl("all")">All</a>
    </li>
</ul>

@if (Model.Rows.Count == 0)
{
    <p class="text-muted">No transfers in this view.</p>
}
else
{
    <table class="table align-middle">
        <thead>
            <tr>
                <th>Requested</th>
                <th>Requester</th>
                <th>Ticket</th>
                <th>Recipient</th>
                <th>Reason</th>
                <th>Status</th>
                <th></th>
            </tr>
        </thead>
        <tbody>
            @foreach (var r in Model.Rows)
            {
                <tr>
                    <td>@r.RequestedAt.ToDisplayDateTime()</td>
                    <td><vc:human user-id="@r.SenderUserId" layout="AvatarName" link="Admin" /></td>
                    <td>
                        @r.OriginalAttendeeName
                        <br/><small class="text-muted">@r.TicketTypeName</small>
                    </td>
                    <td><vc:human user-id="@r.ReceiverUserId" layout="AvatarName" link="Admin" /></td>
                    <td>@r.SenderReason</td>
                    <td>
                        <span class="badge bg-secondary">@r.Status</span>
                        @if (r.Status == Humans.Domain.Enums.TicketTransferStatus.Approved
                             && (r.VendorResult == Humans.Domain.Enums.TicketTransferVendorResult.Failed
                              || r.VendorResult == Humans.Domain.Enums.TicketTransferVendorResult.VoidSucceededIssueFailed))
                        {
                            <span class="badge bg-warning text-dark ms-1">@r.VendorResult</span>
                        }
                    </td>
                    <td>
                        <a class="btn btn-sm btn-primary" asp-action="Detail" asp-route-id="@r.Id">Review</a>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 4: Build + smoke-test**

```
dotnet build Humans.slnx -v quiet
```

In the running app:
- Navigate to `/Tickets/Admin/Transfers` — should show Pending tab default.
- Click "Needs attention" — should switch tab.
- Click "All" — should show every status.
- If any paid order has more attendees than valid+checked-in, the drift card should appear at the top.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/TicketTransferAdminController.cs \
        src/Humans.Web/Views/TicketTransferAdmin/Index.cshtml \
        src/Humans.Web/Models/TicketTransferViewModels.cs
git commit -m "feat(tickets): tabbed transfer Index with drift diagnostic"
```

---

## Task 15: Send page render swap

**Files:**
- Modify: `src/Humans.Web/Views/TicketTransfer/Send.cshtml`
- Modify: `src/Humans.Application/Services/Tickets/TicketTransferService.cs` (one-line comment near `LookupReceiversAsync`)

- [ ] **Step 1: Update the Send view to use `<vc:human>` for rendering**

Replace the multi-match block (the `@if (Model.Receivers.Count > 1)` body) with:

```cshtml
@if (Model.Receivers.Count > 1)
{
    <p class="text-muted">Multiple matches — pick the one you mean.</p>
    <div class="list-group mb-3">
        @foreach (var r in Model.Receivers)
        {
            <form asp-action="Lookup" method="post" class="list-group-item list-group-item-action p-0">
                @Html.AntiForgeryToken()
                <input type="hidden" name="attendeeId" value="@Model.AttendeeId" />
                <input type="hidden" name="query" value="@Model.Query" />
                <input type="hidden" name="selectedUserId" value="@r.UserId" />
                <button type="submit" class="btn btn-link text-decoration-none text-reset w-100 d-flex align-items-center p-3" style="text-align:left">
                    <vc:human user-id="@r.UserId" layout="AvatarName" link="None" size="48" />
                </button>
            </form>
        }
    </div>
}
```

Replace the single-match `card` block with:

```cshtml
else if (Model.Receivers.Count == 1)
{
    var receiver = Model.Receivers[0];
    <div class="mb-3">
        <vc:human user-id="@receiver.UserId" layout="Card" link="None" />
    </div>

    <form asp-action="Submit" method="post">
        @Html.AntiForgeryToken()
        <input type="hidden" name="AttendeeId" value="@Model.AttendeeId" />
        <input type="hidden" name="ReceiverUserId" value="@receiver.UserId" />
        <div class="mb-3">
            <label for="Reason">Reason for transfer (visible to admin)</label>
            <textarea id="Reason" name="Reason" rows="3" class="form-control" required maxlength="1000">@Model.PrefilledReason</textarea>
        </div>
        <button type="submit" class="btn btn-primary">Send ticket</button>
        <a class="btn btn-outline-secondary" asp-controller="Home" asp-action="Index">Cancel</a>
    </form>
}
```

- [ ] **Step 2: Document the search-path divergence**

In `TicketTransferService.cs`, just before `LookupReceiversAsync` (line ~60), add the comment:

```csharp
    // Deliberately diverges from /api/profiles/search (the canonical
    // _HumanSearchInput backend). That endpoint matches name+burner only;
    // ticket-transfer senders typically have the recipient's email, not their
    // burner name, so we keep an exact-email match path here. If/when email-
    // exact lands in the canonical search API, this method can be retired in
    // favour of <vc:_HumanSearchInput scope="…" /> on the Send view.
    // See: memory/architecture/person-search.md.
```

- [ ] **Step 3: Build, smoke-test**

```
dotnet build Humans.slnx -v quiet
```

In the app, `/Tickets/Transfers/Send?attendeeId=<your-test-attendee>`:
- Type an email → single match should render as a `<vc:human>` card.
- Type a burner name with multiple matches → list should render with `<vc:human>` rows.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/TicketTransfer/Send.cshtml \
        src/Humans.Application/Services/Tickets/TicketTransferService.cs
git commit -m "feat(tickets): render Send-page receivers via <vc:human>"
```

---

## Task 16: Profile sidebar holdings — self + admin

**Files:**
- Modify: `src/Humans.Web/Views/Profile/Index.cshtml`
- Modify: `src/Humans.Web/Views/Profile/AdminDetail.cshtml`

- [ ] **Step 1: Add holdings to the self-profile right column**

In `src/Humans.Web/Views/Profile/Index.cshtml`, locate the block (around line 157):

```cshtml
    @if (Model.IsOwnProfile)
    {
        <div class="col-md-4">
            <div class="card mb-4">
                <div class="card-header">@Localizer["Profile_QuickActions"]</div>
```

Inject the holdings card immediately before the Quick Actions card:

```cshtml
    @if (Model.IsOwnProfile)
    {
        <div class="col-md-4">
            <vc:ticket-holdings user-id="@Model.UserId" />

            <div class="card mb-4">
                <div class="card-header">@Localizer["Profile_QuickActions"]</div>
```

(Default `show-empty="false"` — when the user has no tickets, no card renders.)

- [ ] **Step 2: Add holdings to the admin profile right column**

In `src/Humans.Web/Views/Profile/AdminDetail.cshtml`, locate the right column (search for the second `<div class="col-md-4">` or equivalent — context: `/Profile/AdminDetail` uses an admin layout with a sidebar). Add at the top of that column:

```cshtml
<vc:ticket-holdings user-id="@Model.UserId" show-empty="true" />
```

(If `AdminDetail.cshtml` doesn't currently have a right column, add one — pattern: enclose the existing main content in `<div class="col-md-8">` and add `<div class="col-md-4">` next to it within the existing `<div class="row">`. Inspect the live page before deciding which placement looks right.)

- [ ] **Step 3: Build, smoke-test**

```
dotnet build Humans.slnx -v quiet
```

Visit `/Profile` (self) and `/Profile/AdminDetail/<id>` as admin → confirm holdings card renders. For a user with no tickets, self view should show nothing extra; admin view should show an empty-state card.

- [ ] **Step 4: Commit + push phase D**

```bash
git add src/Humans.Web/Views/Profile/Index.cshtml \
        src/Humans.Web/Views/Profile/AdminDetail.cshtml
git commit -m "feat(profile): show ticket holdings on profile sidebars"
git push
```

---

## Final verification

- [ ] **Run the full test suite**

```
dotnet test Humans.slnx -v quiet
```
Expected: all green.

- [ ] **Run the full build**

```
dotnet build Humans.slnx -v quiet
```
Expected: clean.

- [ ] **Manual UI walkthrough**

Boot the app (`dotnet run --project src/Humans.Web`) and walk:

1. `/Profile` (self) — holdings card visible if user has tickets/orders.
2. `/Profile/AdminDetail/<some-user-with-tickets>` — admin view of holdings.
3. `/Tickets/Transfers/Send?attendeeId=<…>` — search, multi-match list, single-match card.
4. `/Tickets/Admin/Transfers` — Pending tab default, switch tabs, drift card visible if drift exists.
5. `/Tickets/Admin/Transfers/Detail/<id>` for:
   - A Pending transfer: Decide form visible, timeline shows Requested only.
   - A successful Approved transfer: timeline shows Void+Issue+LocalWriteback all-green; audit log shows Requested+Approved; "View in TicketTailor" button works.
   - An Approved+VoidSucceededIssueFailed transfer (if one can be staged): Retry-issue form visible.

- [ ] **Open the PR**

```bash
git push
gh pr create --title "Ticket transfer UI tweaks + vendor step history" --body "$(cat <<'EOF'
## Summary
- Holdings view component (`<vc:ticket-holdings>`) on profile sidebars + transfer review page
- Canonical `<vc:human>` rendering on Index columns, Detail cards, and Send page
- Structured `VendorStepsJson` log on `TicketTransferRequest` — per-step timeline on the Detail page + `<vc:audit-log>` embed + TicketTailor deep-link
- Index tabs: Pending / Needs attention / All, with an order-drift diagnostic card
- Retry-issue admin action for partial vendor failures (`VoidSucceededIssueFailed`)
- Onward-transfer fix: ownership cascades via `TicketAttendeeOwnership` (attendee.MatchedUserId, falling back to TicketOrder.MatchedUserId)

Spec: `docs/superpowers/specs/2026-05-12-ticket-transfer-ui-history-design.md`
Plan: `docs/superpowers/plans/2026-05-12-ticket-transfer-ui-history.md`

## Test plan
- [ ] `dotnet test Humans.slnx -v quiet` green
- [ ] `/Profile` (self) — holdings card renders for users with tickets
- [ ] `/Profile/AdminDetail/{id}` — holdings card renders with show-empty=true
- [ ] `/Tickets/Transfers/Send` — single-match and multi-match render via `<vc:human>`
- [ ] `/Tickets/Admin/Transfers` — Pending/Needs attention/All tabs work; drift card appears when drift exists
- [ ] `/Tickets/Admin/Transfers/Detail/{id}` — `<vc:human>` cards, holdings under Sender, TT deep-link, timeline, audit embed, retry form (when applicable)
- [ ] Onward transfer A→B→C succeeds; B can transfer the ticket A sent them
- [ ] EF migration applies cleanly on a QA database with pre-existing transfer rows

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review notes

- **Spec coverage:** Every numbered section (1 through 12) in the spec maps to one or more tasks above. §9 (re-sync safety) is informational; no task needed.
- **Type consistency:** `TicketTransferVendorStepKind` enum values (`Void`, `Issue`, `LocalWriteback`, `RetryIssue`, `ManualReconcile`) consistent across DTO definition (Task 4), step appends (Task 5), retry action (Task 8), timeline view (Task 10). `UserTicketHoldings` shape consistent across interface (Task 6), view component (Task 9), and view template.
- **TDD discipline:** Tasks 1, 2, 5, 6, 7, 8 are test-first. Tasks 3 (migration), 9, 10, 11, 12, 13, 14, 15, 16 are view/wiring/config changes where smoke-test-after is the appropriate discipline; no unit tests written for view markup.
