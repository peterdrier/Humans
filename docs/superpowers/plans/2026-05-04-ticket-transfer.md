# Ticket Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a buyer transfer a Ticket Tailor ticket to another Humans user via a request/approve flow, with TT writeback (void + reissue) and full audit trail.

**Architecture:** New `TicketTransferRequest` aggregate with a four-state lifecycle (`Pending` → `Approved` / `Rejected` / `Cancelled`), owned by the Tickets section. Buyer initiates from the existing homepage ticket card; a TicketAdmin approves or rejects from a new review queue. On approval, vendor-side runs `POST /v1/issued_tickets/{id}/void` then `POST /v1/issued_tickets`. On vendor failure, the transfer falls back to Humans-only (Option C) — request still completes locally, an audit row records the vendor failure, and an admin must edit the ticket in the TT dashboard.

**Tech Stack:** .NET (Humans.Application + Humans.Infrastructure + Humans.Web), ASP.NET MVC, EF Core (PostgreSQL), Hangfire, NodaTime. xUnit + Moq for tests.

**Reference docs:**
- Issue: https://github.com/nobodies-collective/Humans/issues/382
- Probe: `docs/superpowers/probes/2026-05-04-tickettailor-write-api.md` (committed on this branch)
- Section invariants: `docs/sections/Tickets.md`
- Architecture rules: `docs/architecture/design-rules.md`
- Memory atoms most relevant: `memory/process/dotnet-verbosity-quiet.md`, `memory/architecture/no-concurrency-tokens.md`

---

## File Structure

**New files:**
- `src/Humans.Domain/Entities/TicketTransferRequest.cs` — aggregate root
- `src/Humans.Domain/Enums/TicketTransferStatus.cs` — state enum
- `src/Humans.Domain/Enums/TicketTransferVendorResult.cs` — outcome of TT writeback
- `src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs`
- `src/Humans.Application/Interfaces/Tickets/ITicketTransferRepository.cs`
- `src/Humans.Application/Services/Tickets/TicketTransferService.cs`
- `src/Humans.Application/DTOs/TicketTransferDtos.cs` — request/response DTOs + recipient-lookup result + vendor record types
- `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs`
- `src/Humans.Infrastructure/Repositories/Tickets/TicketTransferRepository.cs`
- `src/Humans.Infrastructure/Migrations/<timestamp>_AddTicketTransferRequest.cs` (auto-generated)
- `src/Humans.Web/Controllers/TicketTransferController.cs`
- `src/Humans.Web/Models/TicketTransferViewModels.cs`
- `src/Humans.Web/Views/TicketTransfer/Request.cshtml` — buyer flow page
- `src/Humans.Web/Views/TicketTransfer/Confirm.cshtml` — baseball-card confirmation
- `src/Humans.Web/Views/TicketTransfer/Index.cshtml` — admin review queue
- `src/Humans.Web/Views/TicketTransfer/Detail.cshtml` — admin per-request decision page
- `tests/Humans.Application.Tests/Tickets/TicketTransferServiceTests.cs`
- `tests/Humans.Infrastructure.Tests/TicketTailorServiceWriteTests.cs`
- `tests/Humans.Infrastructure.Tests/TicketSyncServiceNullOrderTests.cs`

**Modified files:**
- `src/Humans.Domain/Enums/AuditAction.cs` — append four enum values
- `src/Humans.Application/Interfaces/Tickets/ITicketVendorService.cs` — add `VoidIssuedTicketAsync` + `IssueTicketAsync`
- `src/Humans.Application/DTOs/VendorTicketDto.cs` (or wherever the record is declared) — make `VendorOrderId` nullable, add `Reference` and `Source` fields
- `src/Humans.Application/Services/Tickets/TicketSyncService.cs` — handle null `VendorOrderId` via `reference`-based fallback parent lookup
- `src/Humans.Infrastructure/Services/TicketTailorService.cs` — implement the two new vendor methods
- `src/Humans.Infrastructure/Services/StubTicketVendorService.cs` — implement deterministic stubs
- `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs` — add `GetAttendeesByUserIdAsync` (for homepage card) + `UpsertAttendeeAsync` single-row helper (used at approval time)
- `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` — implement those
- `src/Humans.Web/Models/DashboardViewModel.cs` — add `MyAttendees: IReadOnlyList<MyAttendeeRowVm>` and `PendingTransferOutCount`
- `src/Humans.Web/Models/DashboardViewModel.cs` — add nested `MyAttendeeRowVm` record
- `src/Humans.Web/Controllers/HomeController.cs` (the action that builds `DashboardViewModel`) — populate the new fields
- `src/Humans.Web/Views/Home/Dashboard.cshtml` — render attendee list with per-row Transfer button when count > 1
- `src/Humans.Web/Extensions/Application/TicketsApplicationExtensions.cs` (or whichever DI extension wires Tickets services) — register `ITicketTransferService` + repo
- `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs` — add `PendingTicketTransferCount` for TicketAdmin
- `docs/sections/Tickets.md` — new entity, actors, invariants, triggers

**Out of scope** (kept to keep this plan finite):
- Webhook subscription for `ISSUED_TICKET.UPDATED` (covered in probe's "Open questions" section; can be done later as a separate enhancement).
- TT API rate-limit-specific retry middleware (basic 429 surfacing is in-scope; sophisticated backoff is not).

---

## Cross-cutting conventions

- **Layer discipline (`design-rules.md` §15):** services are in `Humans.Application.Services.Tickets`; they may not import `Microsoft.EntityFrameworkCore` or reference `HumansDbContext`. All DB access goes through `ITicketTransferRepository` (or the existing `ITicketRepository`).
- **No concurrency tokens:** per `memory/architecture/no-concurrency-tokens.md`, do not add `IsConcurrencyToken` / row versioning to the new entity.
- **Build verbosity:** every `dotnet build` / `dotnet test` invocation in this plan uses `-v quiet`.
- **Build/test workflow:** when a step says "Build", run `dotnet build Humans.slnx -v quiet` from the worktree root. When a step says "Run tests", run `dotnet test Humans.slnx -v quiet --filter "<filter>"`.
- **Solution slnx:** repo uses `Humans.slnx`, not `.sln`.
- **Migrations:** generate via `dotnet ef migrations add <Name> --project src/Humans.Infrastructure --startup-project src/Humans.Web -- --no-build` (run from worktree root after a successful build).
- **Commits:** end every task with a commit. Push every 3–5 tasks. Commit messages follow conventional-commit-ish style (e.g. `feat(tickets): add TicketTransferRequest entity (#382)`).

---

## Phase 0 — Research (DONE)

Probe doc committed as `97cefa31` on this branch. **Decision:** Option B (void + reissue) is the path; Option C (Humans-only) is the graceful-degradation fallback when the vendor call fails.

---

## Phase 1 — Domain + Migration

### Task 1.1: Add `TicketTransferStatus` enum

**Files:**
- Create: `src/Humans.Domain/Enums/TicketTransferStatus.cs`

- [ ] **Step 1: Write the enum**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Lifecycle states for a ticket transfer request.
/// Transitions: Pending → Approved | Rejected | Cancelled.
/// Approved/Rejected/Cancelled are terminal.
/// </summary>
public enum TicketTransferStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/TicketTransferStatus.cs
git commit -m "feat(tickets): add TicketTransferStatus enum (#382)"
```

---

### Task 1.2: Add `TicketTransferVendorResult` enum

**Files:**
- Create: `src/Humans.Domain/Enums/TicketTransferVendorResult.cs`

- [ ] **Step 1: Write the enum**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Outcome of the TicketTailor writeback when a transfer is approved.
/// <list type="bullet">
///   <item><see cref="NotAttempted"/> — only set on Pending/Rejected/Cancelled rows.</item>
///   <item><see cref="Succeeded"/> — original ticket voided AND replacement issued.</item>
///   <item><see cref="VoidSucceededIssueFailed"/> — recoverable; admin can retry just the issue half.</item>
///   <item><see cref="Failed"/> — neither leg succeeded; transfer is Option-C-only (admin must edit TT dashboard).</item>
/// </list>
/// </summary>
public enum TicketTransferVendorResult
{
    NotAttempted,
    Succeeded,
    VoidSucceededIssueFailed,
    Failed,
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/TicketTransferVendorResult.cs
git commit -m "feat(tickets): add TicketTransferVendorResult enum (#382)"
```

---

### Task 1.3: Add four `AuditAction` enum values

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs` (append at end of enum, after `StorePaymentRecorded`)

- [ ] **Step 1: Append values**

Add these four lines just before the closing `}` of the enum, after `StorePaymentRecorded,`:

```csharp
    TicketTransferRequested,
    TicketTransferApproved,
    TicketTransferRejected,
    TicketTransferCancelled,
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors. (No exhaustive-switch coverage analyzers in this codebase as of probe-time, so appending should not trip the build.)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat(tickets): add TicketTransfer audit actions (#382)"
```

---

### Task 1.4: Add `TicketTransferRequest` entity

**Files:**
- Create: `src/Humans.Domain/Entities/TicketTransferRequest.cs`

- [ ] **Step 1: Write the entity**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A user-initiated request to transfer a TicketAttendee (issued ticket) from
/// the requester (current ticket holder, must be the order's MatchedUserId) to
/// a target Humans user. Pending until a TicketAdmin approves or rejects;
/// requester may also cancel while still Pending. Approved transfers fire a
/// TicketTailor void+reissue; if that fails, Option-C fallback applies (the
/// request still ends in Approved state but VendorResult records the failure
/// so an admin can edit the TT dashboard manually).
/// </summary>
public class TicketTransferRequest
{
    public Guid Id { get; init; }

    /// <summary>FK to the TicketAttendee being transferred (the original issued ticket).</summary>
    public Guid OriginalTicketAttendeeId { get; init; }

    /// <summary>Navigation to the original attendee row.</summary>
    public TicketAttendee OriginalTicketAttendee { get; set; } = null!;

    /// <summary>Humans user who initiated the transfer (the buyer / current holder).</summary>
    public Guid RequesterUserId { get; init; }

    /// <summary>Target Humans user (recipient).</summary>
    public Guid RecipientUserId { get; init; }

    /// <summary>
    /// Snapshot of the recipient's display name at request time, in case their
    /// profile is renamed between request and approval.
    /// </summary>
    public string RecipientDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Snapshot of the recipient's preferred email at request time. This is
    /// what gets sent to TT as the new attendee's email on reissue.
    /// </summary>
    public string RecipientEmail { get; init; } = string.Empty;

    /// <summary>Free-text reason from the requester (visible to admin).</summary>
    public string RequesterReason { get; init; } = string.Empty;

    /// <summary>Lifecycle state. See <see cref="TicketTransferStatus"/>.</summary>
    public TicketTransferStatus Status { get; set; } = TicketTransferStatus.Pending;

    /// <summary>Vendor writeback outcome. NotAttempted until status is Approved.</summary>
    public TicketTransferVendorResult VendorResult { get; set; } = TicketTransferVendorResult.NotAttempted;

    /// <summary>Optional message captured during the vendor call (error text on failure, hold id on success-with-hold).</summary>
    public string? VendorMessage { get; set; }

    /// <summary>
    /// New TT issued-ticket id, set when the void+reissue succeeded. Null
    /// otherwise. The fresh TicketAttendee row created at approval time will
    /// also carry this in <see cref="TicketAttendee.VendorTicketId"/>.
    /// </summary>
    public string? NewVendorTicketId { get; set; }

    /// <summary>TicketAdmin who decided (null while Pending or if Cancelled by requester).</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Free-text from the deciding admin (rejection reason or approval note).</summary>
    public string? AdminNotes { get; set; }

    public Instant RequestedAt { get; init; }
    public Instant? DecidedAt { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/TicketTransferRequest.cs
git commit -m "feat(tickets): add TicketTransferRequest entity (#382)"
```

---

### Task 1.5: EF configuration for `TicketTransferRequest`

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs`

Reference for snake_case + Instant conventions: read `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketAttendeeConfiguration.cs` if present, otherwise `src/Humans.Infrastructure/Data/Configurations/Tickets/TicketOrderConfiguration.cs`. Match its style.

- [ ] **Step 1: Write the configuration**

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Tickets;

public sealed class TicketTransferRequestConfiguration : IEntityTypeConfiguration<TicketTransferRequest>
{
    public void Configure(EntityTypeBuilder<TicketTransferRequest> builder)
    {
        builder.ToTable("ticket_transfer_requests");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OriginalTicketAttendeeId)
            .IsRequired();

        builder.HasOne(x => x.OriginalTicketAttendee)
            .WithMany() // no inverse collection on TicketAttendee — keep that aggregate clean
            .HasForeignKey(x => x.OriginalTicketAttendeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.RequesterUserId).IsRequired();
        builder.Property(x => x.RecipientUserId).IsRequired();

        builder.Property(x => x.RecipientDisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(x => x.RecipientEmail)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(x => x.RequesterReason)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.VendorResult)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.VendorMessage).HasMaxLength(2000);
        builder.Property(x => x.NewVendorTicketId).HasMaxLength(64);
        builder.Property(x => x.AdminNotes).HasMaxLength(1000);

        builder.Property(x => x.RequestedAt).IsRequired();

        // Indexes:
        // - one Pending row per original attendee (enforces "only one Pending request per ticket")
        builder.HasIndex(x => x.OriginalTicketAttendeeId)
            .IsUnique()
            .HasFilter("status = 'Pending'");

        // - by requester (homepage card)
        builder.HasIndex(x => new { x.RequesterUserId, x.Status });

        // - by status (admin queue)
        builder.HasIndex(x => x.Status);
    }
}
```

- [ ] **Step 2: Register the configuration in `HumansDbContext`**

Open `src/Humans.Infrastructure/Data/HumansDbContext.cs` and either (a) confirm it uses `modelBuilder.ApplyConfigurationsFromAssembly(...)` so the new config is auto-discovered, or (b) add `modelBuilder.ApplyConfiguration(new TicketTransferRequestConfiguration());` next to the existing Tickets configurations.

Also add the DbSet:

```csharp
public DbSet<TicketTransferRequest> TicketTransferRequests => Set<TicketTransferRequest>();
```

(Place alphabetically near other Ticket DbSets.)

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/Tickets/TicketTransferRequestConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(tickets): EF config + DbSet for TicketTransferRequest (#382)"
```

---

### Task 1.6: Generate migration

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddTicketTransferRequest.cs` (auto-generated)
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddTicketTransferRequest.Designer.cs` (auto-generated)
- Modify: `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs` (auto-updated)

- [ ] **Step 1: Generate migration**

Run from worktree root:

```bash
dotnet build Humans.slnx -v quiet
dotnet ef migrations add AddTicketTransferRequest \
    --project src/Humans.Infrastructure \
    --startup-project src/Humans.Web -- --no-build
```

Expected: three new files written (or two new + one modified). No errors.

- [ ] **Step 2: Inspect the generated `Up` method**

Open the generated `<timestamp>_AddTicketTransferRequest.cs` and confirm it:
- Creates table `ticket_transfer_requests` with all columns from the entity
- Adds the partial-unique index `IX_ticket_transfer_requests_OriginalTicketAttendeeId` with filter `status = 'Pending'`
- Adds the FK to `ticket_attendees` with `OnDelete: Restrict`

If anything looks wrong, fix the configuration and regenerate (`dotnet ef migrations remove ... && dotnet ef migrations add ...`).

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 4: Apply locally to confirm SQL is well-formed**

Run from worktree root:

```bash
dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web -- --no-build
```

Expected: migration applies cleanly to the local QA database. (If the local connection-string env var is unset, skip this step and rely on the inspection in Step 2.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(tickets): migration for ticket_transfer_requests (#382)"
git push -u origin issue-382-ticket-transfer
```

---

## Phase 2 — Application service + repository

### Task 2.1: Add transfer DTOs

**Files:**
- Create: `src/Humans.Application/DTOs/TicketTransferDtos.cs`

- [ ] **Step 1: Write DTOs**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

/// <summary>Single match returned by recipient lookup. Null result means "no match".</summary>
public sealed record RecipientLookupResultDto(
    Guid UserId,
    string DisplayName,
    string? BurnerName,
    string? PreferredEmail,
    bool HasCustomProfilePicture,
    string? ProfilePictureUrl);

/// <summary>Submitted by the buyer's recipient-lookup form.</summary>
public sealed record RecipientLookupRequest(string Query);

/// <summary>Submitted by the buyer when confirming the recipient.</summary>
public sealed record TicketTransferRequestDto(
    Guid OriginalAttendeeId,
    Guid RecipientUserId,
    string Reason);

/// <summary>Admin decision payload.</summary>
public sealed record TicketTransferDecisionDto(
    Guid TransferRequestId,
    bool Approve,
    string? AdminNotes);

/// <summary>Read-side DTO for the admin queue.</summary>
public sealed record TicketTransferRowDto(
    Guid Id,
    Guid OriginalAttendeeId,
    string OriginalAttendeeName,
    string TicketTypeName,
    Guid RequesterUserId,
    string RequesterDisplayName,
    Guid RecipientUserId,
    string RecipientDisplayName,
    string RecipientEmail,
    string RequesterReason,
    TicketTransferStatus Status,
    TicketTransferVendorResult VendorResult,
    string? VendorMessage,
    Guid? DecidedByUserId,
    string? DecidedByDisplayName,
    string? AdminNotes,
    Instant RequestedAt,
    Instant? DecidedAt);
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/DTOs/TicketTransferDtos.cs
git commit -m "feat(tickets): add ticket-transfer DTOs (#382)"
```

---

### Task 2.2: `ITicketTransferRepository`

**Files:**
- Create: `src/Humans.Application/Interfaces/Tickets/ITicketTransferRepository.cs`

- [ ] **Step 1: Write the interface**

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Tickets;

public interface ITicketTransferRepository
{
    Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<TicketTransferRequest?> GetPendingForAttendeeAsync(Guid attendeeId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetByRequesterAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    Task AddAsync(TicketTransferRequest request, CancellationToken ct = default);

    Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketTransferRepository.cs
git commit -m "feat(tickets): ITicketTransferRepository interface (#382)"
```

---

### Task 2.3: `TicketTransferRepository` implementation

**Files:**
- Create: `src/Humans.Infrastructure/Repositories/Tickets/TicketTransferRepository.cs`

Pattern reference: read `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` for the constructor + `HumansDbContext` injection idiom and use the same.

- [ ] **Step 1: Write the implementation**

```csharp
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Tickets;

public sealed class TicketTransferRepository : ITicketTransferRepository
{
    private readonly HumansDbContext _db;

    public TicketTransferRepository(HumansDbContext db) => _db = db;

    public Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.TicketTransferRequests
            .Include(r => r.OriginalTicketAttendee)
                .ThenInclude(a => a.TicketOrder)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<TicketTransferRequest?> GetPendingForAttendeeAsync(Guid attendeeId, CancellationToken ct = default) =>
        _db.TicketTransferRequests
            .FirstOrDefaultAsync(r => r.OriginalTicketAttendeeId == attendeeId &&
                                       r.Status == TicketTransferStatus.Pending, ct);

    public async Task<IReadOnlyList<TicketTransferRequest>> GetByRequesterAsync(Guid userId, CancellationToken ct = default) =>
        await _db.TicketTransferRequests
            .Where(r => r.RequesterUserId == userId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default) =>
        await _db.TicketTransferRequests
            .Include(r => r.OriginalTicketAttendee)
                .ThenInclude(a => a.TicketOrder)
            .Where(r => r.Status == status)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        _db.TicketTransferRequests.CountAsync(r => r.Status == TicketTransferStatus.Pending, ct);

    public async Task AddAsync(TicketTransferRequest request, CancellationToken ct = default)
    {
        _db.TicketTransferRequests.Add(request);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default)
    {
        _db.TicketTransferRequests.Update(request);
        await _db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Repositories/Tickets/TicketTransferRepository.cs
git commit -m "feat(tickets): TicketTransferRepository (#382)"
```

---

### Task 2.4: `ITicketTransferService`

**Files:**
- Create: `src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs`

- [ ] **Step 1: Write the interface**

```csharp
using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Tickets;

public interface ITicketTransferService
{
    /// <summary>
    /// Resolve a recipient by email (exact, case-insensitive against UserEmails)
    /// or burner-name wildcard. Returns null for zero or ambiguous matches —
    /// the caller is required to render exactly-one before allowing submission.
    /// </summary>
    Task<RecipientLookupResultDto?> LookupRecipientAsync(
        string query, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>
    /// Create a Pending TicketTransferRequest. Validates: requester owns the
    /// attendee, attendee is Valid, no existing Pending request, recipient is
    /// not the requester, recipient does not already hold a Valid/CheckedIn
    /// ticket for the same event.
    /// </summary>
    Task<TicketTransferRowDto> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>
    /// Cancel a Pending request. Only the original requester may cancel.
    /// </summary>
    Task CancelAsync(Guid transferRequestId, Guid requesterUserId, CancellationToken ct = default);

    /// <summary>
    /// Approve a Pending request. Fires TT void+reissue; falls through to
    /// Option C (Approved + VendorResult.Failed) on vendor failure.
    /// </summary>
    Task<TicketTransferRowDto> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    /// <summary>
    /// Reject a Pending request. No TT call.
    /// </summary>
    Task<TicketTransferRowDto> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRowDto>> GetByRequesterAsync(
        Guid userId, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs
git commit -m "feat(tickets): ITicketTransferService interface (#382)"
```

---

### Task 2.5: Vendor interface additions (signatures only — implementation in Phase 3)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Tickets/ITicketVendorService.cs`
- Create section in: `src/Humans.Application/DTOs/TicketTransferDtos.cs` (append)

- [ ] **Step 1: Add vendor records to `TicketTransferDtos.cs`**

Append to the existing `TicketTransferDtos.cs`:

```csharp
/// <summary>Outcome of a TT void call.</summary>
public sealed record VoidIssuedTicketResult(string VendorTicketId, string? HoldId);

/// <summary>Payload for TT issue-ticket call. EventId+TicketTypeId XOR HoldId is required.</summary>
public sealed record IssueTicketRequest(
    string? EventId,
    string? TicketTypeId,
    string? HoldId,
    string FullName,
    string? Email,
    bool SendEmail,
    string? ExternalReference);

/// <summary>Categorised vendor failure for Option-C fallback decisions in the service.</summary>
public sealed class TicketVendorWriteException : Exception
{
    public TicketVendorWriteException(string message, TicketVendorFailureKind kind, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
    public TicketVendorFailureKind Kind { get; }
}

public enum TicketVendorFailureKind
{
    /// <summary>HTTP 400 / 422 — bad payload, sold out, seated ticket type. Do not retry.</summary>
    Validation,
    /// <summary>HTTP 401/403 — credential rotation problem. Do not retry.</summary>
    AuthFailed,
    /// <summary>HTTP 404 — ticket already voided or unknown. Treat per-call.</summary>
    NotFound,
    /// <summary>HTTP 429 — rate limited. Surface to user; do not auto-retry mid-request.</summary>
    RateLimited,
    /// <summary>HTTP 5xx or transport failure. May retry from admin UI.</summary>
    Transient,
}
```

- [ ] **Step 2: Add the two methods to `ITicketVendorService.cs`**

Append inside the interface (after `GetDiscountCodeUsageAsync`):

```csharp
    /// <summary>
    /// Voids an issued ticket. When <paramref name="voidToHold"/> is true,
    /// returns a hold id that can be passed to <see cref="IssueTicketAsync"/>
    /// so the seat is reissued without racing against open inventory.
    /// </summary>
    Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default);

    /// <summary>
    /// Issues a new ticket. Caller must supply EITHER EventId+TicketTypeId OR
    /// HoldId. Note: TT does NOT associate API-issued tickets with an order
    /// (the resulting ticket has order_id=null and source="api"). Pass the
    /// Humans TicketTransferRequest.Id as <see cref="IssueTicketRequest.ExternalReference"/>
    /// so the next sync can re-link the orphan attendee.
    /// </summary>
    Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default);
```

Add `using Humans.Application.DTOs;` at top if not already present.

- [ ] **Step 3: Build (will FAIL — implementations missing)**

Run: `dotnet build Humans.slnx -v quiet`
Expected: errors about `TicketTailorService` / `StubTicketVendorService` not implementing the new methods. That's the expected state for this commit; Phase 3 fills them in.

To keep the tree compiling between phases, add **temporary throwing stubs** to both classes now:

In `src/Humans.Infrastructure/Services/TicketTailorService.cs`, add at end of class body:

```csharp
    public Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(string vendorTicketId, bool voidToHold, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Phase 3 / Task 3.1");

    public Task<VendorTicketDto> IssueTicketAsync(IssueTicketRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Phase 3 / Task 3.1");
```

Then in `src/Humans.Infrastructure/Services/StubTicketVendorService.cs` add the same two stub methods (still throwing — Task 3.3 fills in deterministic stubs).

- [ ] **Step 4: Build now succeeds**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketVendorService.cs src/Humans.Application/DTOs/TicketTransferDtos.cs src/Humans.Infrastructure/Services/TicketTailorService.cs src/Humans.Infrastructure/Services/StubTicketVendorService.cs
git commit -m "feat(tickets): vendor interface for void+reissue (signatures) (#382)"
git push
```

---

### Task 2.6: `TicketTransferService` skeleton (state machine + validation, vendor wired but not yet exercised)

**Files:**
- Create: `src/Humans.Application/Services/Tickets/TicketTransferService.cs`

**Why this is one task, not five:** the state machine is small enough that splitting Pending-creation, cancel, approve, reject across separate tasks creates more friction than it saves — they share the same private mapping helper. We test each path in Phase 8.

This task wires the service in but keeps `ApproveAsync`'s vendor call to a single internal method `WriteToVendorAsync` so Phase 3 can fill it in cleanly.

The service depends on existing interfaces — verify each is present before writing:
- `ITicketRepository` exists in `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs`. We need: `GetAttendeeByIdAsync(Guid)`, `GetMatchedAttendeesForEventAsync(string, CancellationToken)` (already used by sync), and a new method `UpsertAttendeeAsync(TicketAttendee, CancellationToken)` for single-row insert at approval time. Add `UpsertAttendeeAsync` (and the matching impl on `TicketRepository`) **as part of this task** if missing.
- `IUserService` (already used by `TicketSyncService`) — we need `GetByIdAsync(Guid)` and `GetByIdsAsync(...)`.
- `IUserEmailService` — we need a new method `GetUserIdByExactEmailAsync(string)` for the email-exact-match recipient lookup. Add it (interface + impl) as part of this task if missing.
- `IProfileService` — we need `SearchByBurnerNameAsync(string, int max)` for the burner-name wildcard match. Add it (interface + impl) as part of this task if missing.
- `IAuditLogService` (find by grepping for an existing `WriteAsync` audit pattern) — we need to write four audit actions. Match its existing call shape from `TicketSyncService` or `CampService` (whichever already calls it).
- `IClock` from NodaTime (already used).

If any of `UpsertAttendeeAsync`, `GetUserIdByExactEmailAsync`, `SearchByBurnerNameAsync` are missing: **add the interface signature and implementation in this task**, before writing `TicketTransferService`. Each addition gets a one-line description in `Tickets.md` / `Profiles.md` cross-section deps when we update section docs in Phase 8.

- [ ] **Step 1: Add `UpsertAttendeeAsync` to `ITicketRepository` if missing**

Search `ITicketRepository` for any single-row attendee write. If absent, add:

```csharp
    /// <summary>Insert or update a single TicketAttendee row. Used when the Tickets section creates an attendee outside the sync loop (e.g. on approved transfer reissue).</summary>
    Task UpsertAttendeeAsync(TicketAttendee attendee, CancellationToken ct = default);
```

And implement in `TicketRepository.cs`:

```csharp
    public async Task UpsertAttendeeAsync(TicketAttendee attendee, CancellationToken ct = default)
    {
        var existing = await _db.TicketAttendees
            .FirstOrDefaultAsync(a => a.VendorTicketId == attendee.VendorTicketId, ct);
        if (existing is null)
            _db.TicketAttendees.Add(attendee);
        else
            _db.Entry(existing).CurrentValues.SetValues(attendee);
        await _db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 2: Add `GetUserIdByExactEmailAsync` to `IUserEmailService` if missing**

Search `IUserEmailService` for an exact-email lookup. If absent, add:

```csharp
    /// <summary>Resolve a user by exact, case-insensitive email match against UserEmails. Returns null if zero or ambiguous matches.</summary>
    Task<Guid?> GetUserIdByExactEmailAsync(string email, CancellationToken ct = default);
```

Implementation lives in whatever class implements `IUserEmailService` (search for `class UserEmailService` or `: IUserEmailService`). Use the existing `NormalizingEmailComparer` pattern shown in `TicketSyncService.BuildEmailLookupAsync`. Ambiguous → return null.

- [ ] **Step 3: Add `SearchByBurnerNameAsync` to `IProfileService` if missing**

```csharp
    /// <summary>Wildcard-match profiles by BurnerName (case-insensitive contains). Returns up to <paramref name="maxResults"/> rows. Used by ticket-transfer recipient lookup.</summary>
    Task<IReadOnlyList<ProfileSearchResultDto>> SearchByBurnerNameAsync(string query, int maxResults, CancellationToken ct = default);
```

Add a `ProfileSearchResultDto` DTO with at minimum `(Guid UserId, string DisplayName, string? BurnerName, bool HasCustomProfilePicture)`. Reuse an existing DTO if it already covers this shape.

Implementation: filter `Profiles.Where(p => EF.Functions.ILike(p.BurnerName, "%" + query + "%"))` (PostgreSQL ILike is already used elsewhere in the codebase — grep for `EF.Functions.ILike` to confirm and match style).

- [ ] **Step 4: Write `TicketTransferService`**

```csharp
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Owns the TicketTransferRequest aggregate's lifecycle. Buyer initiates,
/// admin decides; on approval, attempts a TicketTailor void+reissue and falls
/// back to Option-C (Humans-only, admin must edit dashboard) on vendor failure.
/// </summary>
public sealed class TicketTransferService : ITicketTransferService
{
    private readonly ITicketTransferRepository _transferRepo;
    private readonly ITicketRepository _ticketRepo;
    private readonly ITicketVendorService _vendor;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLog;
    private readonly TicketVendorSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<TicketTransferService> _logger;

    public TicketTransferService(
        ITicketTransferRepository transferRepo,
        ITicketRepository ticketRepo,
        ITicketVendorService vendor,
        IUserService userService,
        IUserEmailService userEmailService,
        IProfileService profileService,
        IAuditLogService auditLog,
        IOptions<TicketVendorSettings> settings,
        IClock clock,
        ILogger<TicketTransferService> logger)
    {
        _transferRepo = transferRepo;
        _ticketRepo = ticketRepo;
        _vendor = vendor;
        _userService = userService;
        _userEmailService = userEmailService;
        _profileService = profileService;
        _auditLog = auditLog;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RecipientLookupResultDto?> LookupRecipientAsync(
        string query, Guid requesterUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var trimmed = query.Trim();

        // Heuristic: contains '@' → email exact match; else burner-name wildcard.
        if (trimmed.Contains('@'))
        {
            var userId = await _userEmailService.GetUserIdByExactEmailAsync(trimmed, ct);
            if (userId is null || userId == requesterUserId) return null;
            return await BuildRecipientCardAsync(userId.Value, ct);
        }

        var hits = await _profileService.SearchByBurnerNameAsync(trimmed, maxResults: 2, ct);
        var filtered = hits.Where(h => h.UserId != requesterUserId).ToList();
        if (filtered.Count != 1) return null;
        return await BuildRecipientCardAsync(filtered[0].UserId, ct);
    }

    public async Task<TicketTransferRowDto> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid requesterUserId, CancellationToken ct = default)
    {
        if (dto.RecipientUserId == requesterUserId)
            throw new InvalidOperationException("Cannot transfer a ticket to yourself.");

        var attendee = await _ticketRepo.GetAttendeeByIdAsync(dto.OriginalAttendeeId, ct)
            ?? throw new InvalidOperationException("Attendee not found.");

        // Requester must own the parent order's MatchedUserId (the buyer can transfer
        // any attendee in their order, even ones whose AttendeeEmail matches another user).
        if (attendee.TicketOrder.MatchedUserId != requesterUserId)
            throw new InvalidOperationException("You can only transfer tickets from your own orders.");

        if (attendee.Status != TicketAttendeeStatus.Valid)
            throw new InvalidOperationException("Only Valid tickets can be transferred.");

        var existingPending = await _transferRepo.GetPendingForAttendeeAsync(dto.OriginalAttendeeId, ct);
        if (existingPending is not null)
            throw new InvalidOperationException("A pending transfer already exists for this ticket.");

        // Recipient must not already hold a Valid/CheckedIn ticket for this event.
        var recipientAttendees = await _ticketRepo
            .GetMatchedAttendeesForEventAsync(attendee.VendorEventId, ct);
        if (recipientAttendees.Any(a => a.MatchedUserId == dto.RecipientUserId &&
            (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn)))
        {
            throw new InvalidOperationException("Recipient already holds a ticket for this event.");
        }

        var recipientUser = await _userService.GetByIdAsync(dto.RecipientUserId, ct)
            ?? throw new InvalidOperationException("Recipient user not found.");
        var recipientEmail = await _userEmailService.GetPrimaryEmailAsync(dto.RecipientUserId, ct)
            ?? throw new InvalidOperationException("Recipient has no primary email on file.");

        var now = _clock.GetCurrentInstant();
        var request = new TicketTransferRequest
        {
            Id = Guid.NewGuid(),
            OriginalTicketAttendeeId = dto.OriginalAttendeeId,
            RequesterUserId = requesterUserId,
            RecipientUserId = dto.RecipientUserId,
            RecipientDisplayName = recipientUser.DisplayName,
            RecipientEmail = recipientEmail,
            RequesterReason = dto.Reason ?? string.Empty,
            Status = TicketTransferStatus.Pending,
            VendorResult = TicketTransferVendorResult.NotAttempted,
            RequestedAt = now,
        };

        await _transferRepo.AddAsync(request, ct);

        await _auditLog.WriteAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.TicketTransferRequested,
            EntityType = nameof(TicketTransferRequest),
            EntityId = request.Id,
            Description = $"Transfer requested: ticket {attendee.VendorTicketId} → {recipientUser.DisplayName}",
            OccurredAt = now,
            ActorUserId = requesterUserId,
            RelatedEntityId = dto.RecipientUserId,
            RelatedEntityType = nameof(User),
        }, ct);

        return await BuildRowDtoAsync(request, ct);
    }

    public async Task CancelAsync(Guid transferRequestId, Guid requesterUserId, CancellationToken ct = default)
    {
        var request = await _transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be cancelled.");
        if (request.RequesterUserId != requesterUserId)
            throw new InvalidOperationException("Only the requester can cancel.");

        var now = _clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Cancelled;
        request.DecidedAt = now;
        await _transferRepo.UpdateAsync(request, ct);

        await _auditLog.WriteAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.TicketTransferCancelled,
            EntityType = nameof(TicketTransferRequest),
            EntityId = request.Id,
            Description = "Transfer cancelled by requester",
            OccurredAt = now,
            ActorUserId = requesterUserId,
        }, ct);
    }

    public async Task<TicketTransferRowDto> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await _transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");

        var now = _clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Rejected;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = adminNotes;
        await _transferRepo.UpdateAsync(request, ct);

        await _auditLog.WriteAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.TicketTransferRejected,
            EntityType = nameof(TicketTransferRequest),
            EntityId = request.Id,
            Description = $"Transfer rejected{(string.IsNullOrEmpty(adminNotes) ? "" : ": " + adminNotes)}",
            OccurredAt = now,
            ActorUserId = adminUserId,
            RelatedEntityId = request.RequesterUserId,
            RelatedEntityType = nameof(User),
        }, ct);

        return await BuildRowDtoAsync(request, ct);
    }

    public async Task<TicketTransferRowDto> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await _transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");

        var now = _clock.GetCurrentInstant();

        // Vendor writeback (Option B). On any vendor failure, fall back to
        // Option C (mark Approved + record vendor failure for admin to fix in dashboard).
        await WriteToVendorAsync(request, ct);

        request.Status = TicketTransferStatus.Approved;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = adminNotes;
        await _transferRepo.UpdateAsync(request, ct);

        await _auditLog.WriteAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.TicketTransferApproved,
            EntityType = nameof(TicketTransferRequest),
            EntityId = request.Id,
            Description = request.VendorResult switch
            {
                TicketTransferVendorResult.Succeeded =>
                    $"Transfer approved (TT void+reissue OK, new ticket {request.NewVendorTicketId})",
                TicketTransferVendorResult.VoidSucceededIssueFailed =>
                    $"Transfer approved (TT void OK, reissue FAILED: {request.VendorMessage}) — manual reissue needed",
                TicketTransferVendorResult.Failed =>
                    $"Transfer approved (TT writeback FAILED: {request.VendorMessage}) — Option-C fallback, edit ticket in TT dashboard",
                _ => "Transfer approved"
            },
            OccurredAt = now,
            ActorUserId = adminUserId,
            RelatedEntityId = request.RequesterUserId,
            RelatedEntityType = nameof(User),
        }, ct);

        return await BuildRowDtoAsync(request, ct);
    }

    public async Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default)
    {
        var rows = await _transferRepo.GetByStatusAsync(status, ct);
        var result = new List<TicketTransferRowDto>(rows.Count);
        foreach (var r in rows)
            result.Add(await BuildRowDtoAsync(r, ct));
        return result;
    }

    public async Task<IReadOnlyList<TicketTransferRowDto>> GetByRequesterAsync(
        Guid userId, CancellationToken ct = default)
    {
        var rows = await _transferRepo.GetByRequesterAsync(userId, ct);
        var result = new List<TicketTransferRowDto>(rows.Count);
        foreach (var r in rows)
            result.Add(await BuildRowDtoAsync(r, ct));
        return result;
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        _transferRepo.CountPendingAsync(ct);

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 3 wires real TT calls in here. For Phase 2 this method runs the
    /// state machine without actually calling the vendor, marking VendorResult
    /// as NotAttempted. The wiring is done in Phase 3 / Task 3.4.
    /// </summary>
    private async Task WriteToVendorAsync(TicketTransferRequest request, CancellationToken ct)
    {
        // PHASE-3-PLACEHOLDER: leave as no-op. Phase 3 / Task 3.4 replaces this
        // with the real void+reissue flow; tests in Phase 8 cover both branches.
        await Task.CompletedTask;
        request.VendorResult = TicketTransferVendorResult.NotAttempted;
    }

    private async Task<RecipientLookupResultDto?> BuildRecipientCardAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null) return null;
        var profile = await _profileService.GetProfileAsync(userId);
        var primary = await _userEmailService.GetPrimaryEmailAsync(userId, ct);
        return new RecipientLookupResultDto(
            UserId: userId,
            DisplayName: user.DisplayName,
            BurnerName: profile?.BurnerName,
            PreferredEmail: primary,
            HasCustomProfilePicture: profile?.HasCustomProfilePicture ?? false,
            ProfilePictureUrl: user.ProfilePictureUrl);
    }

    private async Task<TicketTransferRowDto> BuildRowDtoAsync(TicketTransferRequest r, CancellationToken ct)
    {
        var requester = await _userService.GetByIdAsync(r.RequesterUserId, ct);
        var decider = r.DecidedByUserId is null ? null : await _userService.GetByIdAsync(r.DecidedByUserId.Value, ct);
        var attendee = r.OriginalTicketAttendee
            ?? await _ticketRepo.GetAttendeeByIdAsync(r.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

        return new TicketTransferRowDto(
            Id: r.Id,
            OriginalAttendeeId: r.OriginalTicketAttendeeId,
            OriginalAttendeeName: attendee.AttendeeName,
            TicketTypeName: attendee.TicketTypeName,
            RequesterUserId: r.RequesterUserId,
            RequesterDisplayName: requester?.DisplayName ?? "(unknown)",
            RecipientUserId: r.RecipientUserId,
            RecipientDisplayName: r.RecipientDisplayName,
            RecipientEmail: r.RecipientEmail,
            RequesterReason: r.RequesterReason,
            Status: r.Status,
            VendorResult: r.VendorResult,
            VendorMessage: r.VendorMessage,
            DecidedByUserId: r.DecidedByUserId,
            DecidedByDisplayName: decider?.DisplayName,
            AdminNotes: r.AdminNotes,
            RequestedAt: r.RequestedAt,
            DecidedAt: r.DecidedAt);
    }
}
```

If `IUserService.GetByIdAsync` does not have a `CancellationToken` overload, drop the `ct` argument from those calls (or add the overload — either is fine; match what the codebase already does).

If `IUserEmailService.GetPrimaryEmailAsync` does not exist, search for the existing primary-email lookup (likely something like `GetPrimaryEmailForUserAsync` or `GetPreferredEmailAsync`) and use that name instead.

- [ ] **Step 5: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors. Resolve any naming mismatches with existing services as you go.

- [ ] **Step 6: DI registration**

Open `src/Humans.Web/Extensions/Application/TicketsApplicationExtensions.cs` (or whichever file already wires `TicketSyncService` / `TicketQueryService` — grep for `AddScoped<ITicketSyncService` and add next to it):

```csharp
services.AddScoped<ITicketTransferService, TicketTransferService>();
services.AddScoped<ITicketTransferRepository, TicketTransferRepository>();
```

The repo registration may belong in the Infrastructure DI extension instead (search for where `TicketRepository` is registered). Match that pattern.

- [ ] **Step 7: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 8: Commit + push**

```bash
git add src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs src/Humans.Application/Services/Tickets/TicketTransferService.cs src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs src/Humans.Application/Interfaces/Profiles/IProfileService.cs src/Humans.Application/Services/Profiles/ProfileService.cs src/Humans.Application/Interfaces/Users/IUserEmailService.cs src/Humans.Application/Services/Users/UserEmailService.cs src/Humans.Web/Extensions
git commit -m "feat(tickets): TicketTransferService state machine + recipient lookup (#382)"
git push
```

(Adjust `git add` paths to match where you actually edited.)

---

## Phase 3 — TT vendor methods + Option-C fallback wiring

### Task 3.1: Implement `VoidIssuedTicketAsync` on `TicketTailorService`

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TicketTailorService.cs`

- [ ] **Step 1: Replace the throwing stub with the real implementation**

```csharp
    public async Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/issued_tickets/{vendorTicketId}/void";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["void_to_hold"] = voidToHold ? "true" : "false",
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TicketVendorWriteException(
                $"TicketTailor void transport failure: {ex.Message}",
                TicketVendorFailureKind.Transient, ex);
        }

        if (!response.IsSuccessStatusCode)
            throw await BuildVendorWriteExceptionAsync(response, "void", vendorTicketId, ct);

        var body = await response.Content.ReadFromJsonAsync<TtVoidResponse>(JsonOptions, ct);
        return new VoidIssuedTicketResult(
            VendorTicketId: body?.Id ?? vendorTicketId,
            HoldId: body?.HoldId);
    }
```

- [ ] **Step 2: Add the response DTO and helper near the bottom of the class**

```csharp
    internal sealed record TtVoidResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("hold_id")] string? HoldId,
        [property: JsonPropertyName("voided")] string? Voided);

    private static async Task<TicketVendorWriteException> BuildVendorWriteExceptionAsync(
        HttpResponseMessage response, string op, string subject, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var kind = (int)response.StatusCode switch
        {
            400 or 422 => TicketVendorFailureKind.Validation,
            401 or 403 => TicketVendorFailureKind.AuthFailed,
            404 => TicketVendorFailureKind.NotFound,
            429 => TicketVendorFailureKind.RateLimited,
            >= 500 => TicketVendorFailureKind.Transient,
            _ => TicketVendorFailureKind.Transient,
        };
        return new TicketVendorWriteException(
            $"TicketTailor {op} {subject} returned {(int)response.StatusCode}: {body}", kind);
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/TicketTailorService.cs
git commit -m "feat(tickets): TT void-issued-ticket implementation (#382)"
```

---

### Task 3.2: Implement `IssueTicketAsync` on `TicketTailorService`

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TicketTailorService.cs`

- [ ] **Step 1: Replace the throwing stub**

```csharp
    public async Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.HoldId) &&
            (string.IsNullOrEmpty(request.EventId) || string.IsNullOrEmpty(request.TicketTypeId)))
        {
            throw new ArgumentException(
                "IssueTicketRequest requires either HoldId or both EventId and TicketTypeId.");
        }

        var form = new Dictionary<string, string>
        {
            ["full_name"] = request.FullName,
            ["send_email"] = request.SendEmail ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(request.HoldId))
            form["hold_id"] = request.HoldId;
        else
        {
            form["event_id"] = request.EventId!;
            form["ticket_type_id"] = request.TicketTypeId!;
        }
        if (!string.IsNullOrEmpty(request.Email)) form["email"] = request.Email;
        if (!string.IsNullOrEmpty(request.ExternalReference)) form["reference"] = request.ExternalReference;

        using var content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync($"{BaseUrl}/issued_tickets", content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TicketVendorWriteException(
                $"TicketTailor issue transport failure: {ex.Message}",
                TicketVendorFailureKind.Transient, ex);
        }

        if (!response.IsSuccessStatusCode)
            throw await BuildVendorWriteExceptionAsync(response, "issue", request.FullName, ct);

        var body = await response.Content.ReadFromJsonAsync<TtIssuedTicket>(JsonOptions, ct)
            ?? throw new TicketVendorWriteException(
                "TicketTailor issue returned 2xx with empty body",
                TicketVendorFailureKind.Transient);

        return new VendorTicketDto(
            VendorTicketId: body.Id,
            VendorOrderId: body.OrderId, // null for API-issued tickets — sync fix in Phase 4 handles this
            AttendeeName: body.FullName ?? $"{body.FirstName} {body.LastName}".Trim(),
            AttendeeEmail: body.Email,
            TicketTypeName: body.Description ?? "Unknown",
            Price: (body.ListedPrice ?? 0) / 100m,
            Status: body.Status ?? "valid");
    }
```

(`VendorTicketDto.VendorOrderId` becomes nullable in Phase 4 / Task 4.1 — for now, if its current type is non-nullable `string`, pass `body.OrderId ?? string.Empty` to keep this task building, then loosen in Phase 4. Either order works; doing it in this order means a one-line change to this method later.)

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Services/TicketTailorService.cs
git commit -m "feat(tickets): TT create-issued-ticket implementation (#382)"
```

---

### Task 3.3: Implement deterministic stubs on `StubTicketVendorService`

**Files:**
- Modify: `src/Humans.Infrastructure/Services/StubTicketVendorService.cs`

The stub must mirror real-vendor behaviour deterministically so dev/QA/preview test coverage stays meaningful. Read the existing class to see how it stores/returns its in-memory fixture (likely a `List<VendorTicketDto>` field). The stubs need to:

- `VoidIssuedTicketAsync`: find the ticket by id; mark its `Status` to `"voided"`; if `voidToHold` is true, return a synthetic `HoldId` like `"hold_stub_<guid8>"`; if not, return null hold.
- `IssueTicketAsync`: append a new `VendorTicketDto` with a fresh `VendorTicketId` (`"tt_stub_<guid8>"`), `VendorOrderId = null`, the supplied `FullName`/`Email`/`TicketTypeName` (look up by `TicketTypeId` or default to "Stub Ticket"), `Status = "valid"`, then return it.

- [ ] **Step 1: Replace the throwing stubs with the deterministic implementations**

(Sketch — adapt to the actual field names used in the existing stub class.)

```csharp
    public Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default)
    {
        var existing = _fixtureTickets.FirstOrDefault(t => t.VendorTicketId == vendorTicketId);
        if (existing is null)
            throw new TicketVendorWriteException(
                $"Stub: ticket {vendorTicketId} not found",
                TicketVendorFailureKind.NotFound);

        var index = _fixtureTickets.IndexOf(existing);
        _fixtureTickets[index] = existing with { Status = "voided" };

        var holdId = voidToHold ? $"hold_stub_{Guid.NewGuid().ToString("N")[..8]}" : null;
        return Task.FromResult(new VoidIssuedTicketResult(vendorTicketId, holdId));
    }

    public Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default)
    {
        var newTicket = new VendorTicketDto(
            VendorTicketId: $"tt_stub_{Guid.NewGuid().ToString("N")[..8]}",
            VendorOrderId: null, // matches real TT behaviour for API-issued tickets
            AttendeeName: request.FullName,
            AttendeeEmail: request.Email,
            TicketTypeName: "Stub Reissued Ticket",
            Price: 0m,
            Status: "valid");

        _fixtureTickets.Add(newTicket);
        return Task.FromResult(newTicket);
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Services/StubTicketVendorService.cs
git commit -m "feat(tickets): stub vendor void+issue implementations (#382)"
```

---

### Task 3.4: Wire vendor calls into `TicketTransferService.WriteToVendorAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/TicketTransferService.cs`

- [ ] **Step 1: Replace the placeholder `WriteToVendorAsync`**

```csharp
    private async Task WriteToVendorAsync(TicketTransferRequest request, CancellationToken ct)
    {
        var attendee = request.OriginalTicketAttendee
            ?? await _ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing during vendor writeback.");

        // Sub-step 1: void the original (with hold so reissue can't race a sold-out event).
        VoidIssuedTicketResult voidResult;
        try
        {
            voidResult = await _vendor.VoidIssuedTicketAsync(
                attendee.VendorTicketId, voidToHold: true, ct);
        }
        catch (TicketVendorWriteException ex)
        {
            request.VendorResult = TicketTransferVendorResult.Failed;
            request.VendorMessage = $"Void failed ({ex.Kind}): {ex.Message}";
            _logger.LogWarning(ex,
                "TT void failed for transfer {TransferId} attendee {AttendeeId}; falling back to Option-C",
                request.Id, request.OriginalTicketAttendeeId);
            return;
        }

        // Sub-step 2: issue the replacement against the hold.
        VendorTicketDto issued;
        try
        {
            issued = await _vendor.IssueTicketAsync(new IssueTicketRequest(
                EventId: null,
                TicketTypeId: null,
                HoldId: voidResult.HoldId,
                FullName: request.RecipientDisplayName,
                Email: request.RecipientEmail,
                SendEmail: true,
                ExternalReference: request.Id.ToString("N")), ct);
        }
        catch (TicketVendorWriteException ex)
        {
            request.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
            request.VendorMessage = $"Issue failed ({ex.Kind}): {ex.Message} (hold {voidResult.HoldId})";
            _logger.LogError(ex,
                "TT issue failed for transfer {TransferId} after successful void; hold {HoldId} retained",
                request.Id, voidResult.HoldId);
            return;
        }

        // Sub-step 3: pre-populate the new TicketAttendee row so the homepage card
        // updates immediately for the recipient. The next sync will upsert by VendorTicketId.
        var now = _clock.GetCurrentInstant();
        await _ticketRepo.UpsertAttendeeAsync(new TicketAttendee
        {
            Id = Guid.NewGuid(),
            VendorTicketId = issued.VendorTicketId,
            TicketOrderId = attendee.TicketOrderId, // attach to the original order locally
            AttendeeName = request.RecipientDisplayName,
            AttendeeEmail = request.RecipientEmail,
            TicketTypeName = attendee.TicketTypeName,
            Price = attendee.Price, // local snapshot — TT may rebill differently, see probe Open Questions
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = attendee.VendorEventId,
            SyncedAt = now,
            MatchedUserId = request.RecipientUserId,
        }, ct);

        // Pre-populate locally that the original is now Void so the requester's card flips immediately.
        attendee.Status = TicketAttendeeStatus.Void;
        await _ticketRepo.UpsertAttendeeAsync(attendee, ct);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = voidResult.HoldId is null ? null : $"hold {voidResult.HoldId}";
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors. (`UpsertAttendeeAsync` was added in Task 2.6.)

- [ ] **Step 3: Commit + push**

```bash
git add src/Humans.Application/Services/Tickets/TicketTransferService.cs
git commit -m "feat(tickets): wire TT void+reissue into transfer approval (#382)"
git push
```

---

## Phase 4 — Sync resilience (handle null `order_id` from API-issued tickets)

### Task 4.1: Make `VendorTicketDto.VendorOrderId` nullable

**Files:**
- Modify: wherever `VendorTicketDto` is declared (search: `record VendorTicketDto`).
- Modify: `src/Humans.Infrastructure/Services/TicketTailorService.cs:140` (or wherever the read mapping is) — already returns `ticket.OrderId` which may be null, so just remove the `?? string.Empty` if it was added in Task 3.2.
- Modify: `src/Humans.Infrastructure/Services/StubTicketVendorService.cs` — already passes `null` in Task 3.3, will need to confirm the field type is now nullable.

- [ ] **Step 1: Loosen the type**

Change `VendorOrderId: string` to `VendorOrderId: string?` in the record declaration.

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: a handful of nullable-reference warnings/errors at call sites where `dto.VendorOrderId` is dereferenced. Fix each by guarding for null (Task 4.2 fixes the main one). Other call sites may need `?? string.Empty` if they're persistence-only rebuilds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/DTOs/  src/Humans.Infrastructure/Services/TicketTailorService.cs  src/Humans.Infrastructure/Services/StubTicketVendorService.cs
git commit -m "refactor(tickets): VendorTicketDto.VendorOrderId is nullable (#382)"
```

---

### Task 4.2: Update `TicketSyncService` to handle null `VendorOrderId`

**Files:**
- Modify: `src/Humans.Application/Services/Tickets/TicketSyncService.cs:139-156`

- [ ] **Step 1: Replace the foreach body**

Replace lines 141-156 (`foreach (var dto in tickets) { ... }`) with:

```csharp
            foreach (var dto in tickets)
            {
                Guid parentOrderId;

                if (dto.VendorOrderId is { Length: > 0 })
                {
                    if (!orderIdByVendorId.TryGetValue(dto.VendorOrderId, out parentOrderId))
                    {
                        _logger.LogWarning(
                            "Attendee {VendorTicketId} references unknown order {VendorOrderId}, skipping",
                            dto.VendorTicketId, dto.VendorOrderId);
                        continue;
                    }
                }
                else
                {
                    // API-issued ticket (e.g. via transfer reissue) — no order id from vendor.
                    // Prefer existing local row's parent order; otherwise treat as orphan and skip.
                    var localExisting = existingAttendeesByVendorId.GetValueOrDefault(dto.VendorTicketId);
                    if (localExisting is null)
                    {
                        _logger.LogWarning(
                            "Attendee {VendorTicketId} has null vendor order id and no local row; skipping",
                            dto.VendorTicketId);
                        continue;
                    }
                    parentOrderId = localExisting.TicketOrderId;
                }

                var attendee = BuildAttendeeEntity(dto, eventId, emailLookup, now, parentOrderId,
                    existingAttendeesByVendorId.GetValueOrDefault(dto.VendorTicketId));
                attendeesToUpsert.Add(attendee);
                if (attendee.MatchedUserId.HasValue)
                    attendeesMatched++;
            }
```

The "prefer existing local row's parent" branch handles the void+reissue case correctly: Phase 3 / Task 3.4 pre-populates the new attendee with `TicketOrderId = original.TicketOrderId`, so by the time the next sync runs the row is already linked locally and the sync just upserts other fields.

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Services/Tickets/TicketSyncService.cs
git commit -m "fix(tickets): sync no longer drops API-issued tickets with null order_id (#382)"
git push
```

---

## Phase 5 — Buyer-side homepage ticket card

Reference: read `src/Humans.Web/Views/Home/Dashboard.cshtml` and find the existing ticket-card section (search for `HasTicket` / `UserTicketCount` / `TicketPurchaseUrl`). The new attendee list renders inside that card.

### Task 5.1: Extend `DashboardViewModel` with attendee list

**Files:**
- Modify: `src/Humans.Web/Models/DashboardViewModel.cs`

- [ ] **Step 1: Append nested record + new fields**

Add at the bottom of the file (inside the namespace, outside the class):

```csharp
public sealed record MyAttendeeRowVm(
    Guid AttendeeId,
    string AttendeeName,
    string TicketTypeName,
    bool CanRequestTransfer,
    bool HasPendingOutgoingTransfer,
    Guid? PendingTransferRequestId);
```

Inside `DashboardViewModel`, append:

```csharp
    /// <summary>Attendees on the user's orders (rendered when count > 1).</summary>
    public IReadOnlyList<MyAttendeeRowVm> MyAttendees { get; set; } = [];

    /// <summary>How many transfer requests this user has currently in Pending state.</summary>
    public int PendingTransferOutCount { get; set; }
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/DashboardViewModel.cs
git commit -m "feat(tickets): DashboardViewModel exposes user's attendees (#382)"
```

---

### Task 5.2: Populate `MyAttendees` in `HomeController.Dashboard`

**Files:**
- Modify: `src/Humans.Web/Controllers/HomeController.cs` (the action that returns `Dashboard.cshtml`)
- Modify: `src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs` — add `GetAttendeesForBuyerAsync(Guid buyerUserId, CancellationToken)`
- Modify: `src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs` — implement

- [ ] **Step 1: Add `GetAttendeesForBuyerAsync` to the repo**

Interface:

```csharp
    /// <summary>
    /// Get all TicketAttendees on orders where the buyer (TicketOrder.MatchedUserId) is this user.
    /// Used by the homepage ticket card to enumerate transferable attendees.
    /// </summary>
    Task<IReadOnlyList<TicketAttendee>> GetAttendeesForBuyerAsync(Guid buyerUserId, CancellationToken ct = default);
```

Implementation:

```csharp
    public async Task<IReadOnlyList<TicketAttendee>> GetAttendeesForBuyerAsync(Guid buyerUserId, CancellationToken ct = default)
    {
        return await _db.TicketAttendees
            .Include(a => a.TicketOrder)
            .Where(a => a.TicketOrder.MatchedUserId == buyerUserId)
            .OrderBy(a => a.AttendeeName)
            .ToListAsync(ct);
    }
```

- [ ] **Step 2: Wire into `HomeController.Dashboard`**

Inject `ITicketRepository` and `ITicketTransferService` if not already present. In the action, after the existing ticket-card population:

```csharp
        var buyerAttendees = await _ticketRepository.GetAttendeesForBuyerAsync(currentUserId, ct);

        var pendingTransfers = (await _ticketTransferService.GetByRequesterAsync(currentUserId, ct))
            .Where(t => t.Status == TicketTransferStatus.Pending)
            .ToDictionary(t => t.OriginalAttendeeId, t => t.Id);

        viewModel.MyAttendees = buyerAttendees
            .Select(a => new MyAttendeeRowVm(
                AttendeeId: a.Id,
                AttendeeName: a.AttendeeName,
                TicketTypeName: a.TicketTypeName,
                CanRequestTransfer: a.Status == TicketAttendeeStatus.Valid &&
                                    !pendingTransfers.ContainsKey(a.Id),
                HasPendingOutgoingTransfer: pendingTransfers.ContainsKey(a.Id),
                PendingTransferRequestId: pendingTransfers.GetValueOrDefault(a.Id)))
            .ToList();

        viewModel.PendingTransferOutCount = pendingTransfers.Count;
```

(Adjust to whatever pattern the controller currently uses for the rest of the model.)

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/HomeController.cs src/Humans.Application/Interfaces/Repositories/ITicketRepository.cs src/Humans.Infrastructure/Repositories/Tickets/TicketRepository.cs
git commit -m "feat(tickets): populate dashboard with buyer attendees + pending transfers (#382)"
```

---

### Task 5.3: Render attendee list in `Dashboard.cshtml`

**Files:**
- Modify: `src/Humans.Web/Views/Home/Dashboard.cshtml` (find the existing ticket-card section)

- [ ] **Step 1: Inside the existing ticket card, when `Model.MyAttendees.Count > 1`, render the list**

Pattern reference: any existing card in the same view that renders a list with per-row actions — match its Bootstrap classes. The general shape:

```cshtml
@if (Model.MyAttendees.Count > 1)
{
    <ul class="list-group list-group-flush mt-2">
        @foreach (var att in Model.MyAttendees)
        {
            <li class="list-group-item d-flex align-items-center justify-content-between">
                <div>
                    <div>@att.AttendeeName</div>
                    <small class="text-muted">@att.TicketTypeName</small>
                </div>
                <div>
                    @if (att.HasPendingOutgoingTransfer)
                    {
                        <span class="badge bg-warning">@SharedResource.TicketTransfer_Pending</span>
                    }
                    else if (att.CanRequestTransfer)
                    {
                        <a class="btn btn-sm btn-outline-secondary"
                           asp-controller="TicketTransfer" asp-action="Request"
                           asp-route-attendeeId="@att.AttendeeId">
                            @SharedResource.TicketTransfer_RequestButton
                        </a>
                    }
                </div>
            </li>
        }
    </ul>
}
```

(If the codebase uses a different localisation pattern, match that. If localisation strings aren't expected in new strings yet, a literal "Pending review" / "Transfer" is acceptable for now.)

- [ ] **Step 2: Add the two new resource keys to `SharedResource.resx`**

Open `src/Humans.Web/Resources/SharedResource.resx` and add:

- `TicketTransfer_Pending` → "Pending review"
- `TicketTransfer_RequestButton` → "Transfer"

(Spanish/Catalan/etc. translations land in a follow-up; the codebase already tolerates English-only fallback for new strings — confirm by checking how recent additions handled translations.)

- [ ] **Step 3: Build + run dev server, manually verify**

Run from the worktree root:

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

Open the dev server in a browser, log in as a user known to have multiple ticket attendees in the dev fixture (the stub vendor seeds ~450 orders / ~600 tickets per `Tickets.md`). Verify:
- A user with 1 ticket: card unchanged (no list).
- A user with 2+ tickets: card lists attendees, each with a Transfer button.
- Stop the dev server with Ctrl+C.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Home/Dashboard.cshtml src/Humans.Web/Resources/SharedResource.resx
git commit -m "feat(tickets): homepage card lists attendees with transfer button (#382)"
git push
```

---

## Phase 6 — Buyer flow: recipient lookup + baseball-card preview + submit

### Task 6.1: `TicketTransferController` — request + lookup actions

**Files:**
- Create: `src/Humans.Web/Controllers/TicketTransferController.cs`
- Create: `src/Humans.Web/Models/TicketTransferViewModels.cs`
- Create: `src/Humans.Web/Views/TicketTransfer/Request.cshtml`
- Create: `src/Humans.Web/Views/TicketTransfer/Confirm.cshtml`

Pattern reference: any small existing controller that uses `[Authorize]` + per-action policies. `src/Humans.Web/Controllers/TicketController.cs` is the closest neighbour.

- [ ] **Step 1: Write view models**

```csharp
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public sealed class TicketTransferRequestPageViewModel
{
    public Guid AttendeeId { get; set; }
    public string AttendeeName { get; set; } = string.Empty;
    public string TicketTypeName { get; set; } = string.Empty;
    public string? Query { get; set; }
    public RecipientCardViewModel? Recipient { get; set; }
    public string? LookupError { get; set; }
}

public sealed class RecipientCardViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? BurnerName { get; set; }
    public string? PreferredEmail { get; set; }
    public bool HasCustomProfilePicture { get; set; }
    public string? ProfilePictureUrl { get; set; }
}

public sealed class TicketTransferConfirmFormViewModel
{
    public Guid AttendeeId { get; set; }
    public Guid RecipientUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Write the controller**

```csharp
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Tickets/Transfers")]
public sealed class TicketTransferController : Controller
{
    private readonly ITicketTransferService _service;

    public TicketTransferController(ITicketTransferService service) => _service = service;

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("Request")]
    public async Task<IActionResult> Request(Guid attendeeId)
    {
        // Reuse existing TicketRepository via service: read the attendee's name through the homepage data.
        // For minimum surface, pass attendee data via TempData / a lightweight read on the service if needed.
        // Simplest path: have the caller round-trip through a query method. For Phase 6, we trust the
        // server validates ownership at submission time and just renders the form by id.
        var vm = new TicketTransferRequestPageViewModel { AttendeeId = attendeeId };
        return View(vm);
    }

    [HttpPost("Lookup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Lookup(
        Guid attendeeId, string query, CancellationToken ct)
    {
        var match = await _service.LookupRecipientAsync(query, CurrentUserId, ct);
        var vm = new TicketTransferRequestPageViewModel
        {
            AttendeeId = attendeeId,
            Query = query,
            Recipient = match is null ? null : new RecipientCardViewModel
            {
                UserId = match.UserId,
                DisplayName = match.DisplayName,
                BurnerName = match.BurnerName,
                PreferredEmail = match.PreferredEmail,
                HasCustomProfilePicture = match.HasCustomProfilePicture,
                ProfilePictureUrl = match.ProfilePictureUrl,
            },
            LookupError = match is null
                ? "No unique match. Try a full email address or a more specific burner name."
                : null,
        };
        return View(nameof(Request), vm);
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
        TicketTransferConfirmFormViewModel form, CancellationToken ct)
    {
        try
        {
            await _service.CreateRequestAsync(
                new TicketTransferRequestDto(form.AttendeeId, form.RecipientUserId, form.Reason),
                CurrentUserId, ct);
            TempData["Success"] = "Transfer requested. A ticket admin will review it shortly.";
            return RedirectToAction("Dashboard", "Home");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Request), new { attendeeId = form.AttendeeId });
        }
    }

    [HttpPost("Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        try
        {
            await _service.CancelAsync(id, CurrentUserId, ct);
            TempData["Success"] = "Transfer cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction("Dashboard", "Home");
    }
}
```

- [ ] **Step 3: Write `Request.cshtml`**

```cshtml
@model Humans.Web.Models.TicketTransferRequestPageViewModel
@{
    ViewData["Title"] = "Transfer ticket";
}

<h1>Transfer ticket</h1>

@if (TempData["Error"] is string err)
{
    <div class="alert alert-danger">@err</div>
}

<form asp-action="Lookup" method="post" class="mb-3">
    @Html.AntiForgeryToken()
    <input type="hidden" name="attendeeId" value="@Model.AttendeeId" />
    <label for="query">Recipient (full email address, or burner name)</label>
    <div class="input-group">
        <input id="query" name="query" value="@Model.Query" class="form-control" required />
        <button class="btn btn-primary" type="submit">Look up</button>
    </div>
</form>

@if (Model.LookupError is not null)
{
    <div class="alert alert-warning">@Model.LookupError</div>
}

@if (Model.Recipient is not null)
{
    <div class="card mb-3">
        <div class="card-body d-flex align-items-center">
            @if (Model.Recipient.HasCustomProfilePicture && Model.Recipient.ProfilePictureUrl is not null)
            {
                <img src="@Model.Recipient.ProfilePictureUrl" alt="" class="rounded-circle me-3" style="width:64px;height:64px" />
            }
            else
            {
                <div class="me-3 text-muted" style="width:64px;height:64px;display:flex;align-items:center;justify-content:center;background:#eee;border-radius:50%">
                    <strong>@(Model.Recipient.DisplayName.FirstOrDefault())</strong>
                </div>
            }
            <div>
                <div><strong>@Model.Recipient.DisplayName</strong></div>
                @if (Model.Recipient.BurnerName is not null) { <div class="text-muted">@Model.Recipient.BurnerName</div> }
                @if (Model.Recipient.PreferredEmail is not null) { <div class="text-muted">@Model.Recipient.PreferredEmail</div> }
            </div>
        </div>
    </div>

    <form asp-action="Submit" method="post">
        @Html.AntiForgeryToken()
        <input type="hidden" name="AttendeeId" value="@Model.AttendeeId" />
        <input type="hidden" name="RecipientUserId" value="@Model.Recipient.UserId" />
        <div class="mb-3">
            <label for="Reason">Reason for transfer (visible to admin)</label>
            <textarea id="Reason" name="Reason" rows="3" class="form-control" required maxlength="1000"></textarea>
        </div>
        <button type="submit" class="btn btn-primary">Request transfer</button>
        <a class="btn btn-outline-secondary" asp-controller="Home" asp-action="Dashboard">Cancel</a>
    </form>
}
```

(For the buyer flow we render the baseball-card *inline* with the controller-provided fields rather than re-invoking `ProfileCardViewComponent` with `Public` mode — this keeps the recipient surface narrowly to the four fields the user explicitly approved (display name, picture, burner name, optional email) and avoids leaking unrelated profile fields like teams/CV/bio into the transfer flow.)

- [ ] **Step 4: Build + manual smoke**

Run: `dotnet build Humans.slnx -v quiet` then `dotnet run --project src/Humans.Web`. Walk a transfer request end-to-end against a user with 2+ tickets in the stub fixture: lookup a known burner name, confirm card renders, submit, expect redirect to dashboard with success TempData and a "Pending review" badge on the original attendee.

- [ ] **Step 5: Commit + push**

```bash
git add src/Humans.Web/Controllers/TicketTransferController.cs src/Humans.Web/Models/TicketTransferViewModels.cs src/Humans.Web/Views/TicketTransfer/
git commit -m "feat(tickets): buyer-side transfer request flow (#382)"
git push
```

---

## Phase 7 — TicketAdmin review queue

### Task 7.1: Admin index + detail actions on the controller

**Files:**
- Modify: `src/Humans.Web/Controllers/TicketTransferController.cs`
- Create: `src/Humans.Web/Views/TicketTransfer/Index.cshtml`
- Create: `src/Humans.Web/Views/TicketTransfer/Detail.cshtml`

Reference for the policy name: `Tickets.md` documents `TicketAdminBoardOrAdmin` and `TicketAdminOrAdmin`. **Use `TicketAdminOrAdmin`** here (Board can view ticket data but should not be approving transfers — admin-only side effects).

- [ ] **Step 1: Append admin actions to the controller**

```csharp
    [HttpGet("")]
    [Authorize(Policy = "TicketAdminOrAdmin")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var pending = await _service.GetByStatusAsync(TicketTransferStatus.Pending, ct);
        return View(pending);
    }

    [HttpGet("Detail/{id:guid}")]
    [Authorize(Policy = "TicketAdminOrAdmin")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var rows = await _service.GetByStatusAsync(TicketTransferStatus.Pending, ct);
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            // Could be already-decided — fall back to a status lookup across all states.
            // For brevity we just 404 here; admins reach Detail only via the queue link.
            return NotFound();
        }
        return View(row);
    }

    [HttpPost("Decide")]
    [Authorize(Policy = "TicketAdminOrAdmin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(
        Guid id, bool approve, string? adminNotes, CancellationToken ct)
    {
        try
        {
            if (approve)
                await _service.ApproveAsync(id, CurrentUserId, adminNotes, ct);
            else
                await _service.RejectAsync(id, CurrentUserId, adminNotes, ct);
            TempData["Success"] = approve ? "Transfer approved." : "Transfer rejected.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }
```

- [ ] **Step 2: Write `Index.cshtml`**

```cshtml
@model IReadOnlyList<Humans.Application.DTOs.TicketTransferRowDto>
@{
    ViewData["Title"] = "Ticket transfer requests";
}

<h1>Ticket transfer requests <span class="badge bg-secondary">@Model.Count pending</span></h1>

@if (Model.Count == 0)
{
    <p class="text-muted">No pending transfers.</p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>Requested</th>
                <th>From</th>
                <th>To</th>
                <th>Ticket</th>
                <th>Reason</th>
                <th></th>
            </tr>
        </thead>
        <tbody>
            @foreach (var r in Model)
            {
                <tr>
                    <td>@r.RequestedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)</td>
                    <td>@r.RequesterDisplayName</td>
                    <td>@r.RecipientDisplayName<br/><small class="text-muted">@r.RecipientEmail</small></td>
                    <td>@r.OriginalAttendeeName<br/><small class="text-muted">@r.TicketTypeName</small></td>
                    <td>@r.RequesterReason</td>
                    <td><a class="btn btn-sm btn-primary" asp-action="Detail" asp-route-id="@r.Id">Review</a></td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 3: Write `Detail.cshtml`**

```cshtml
@model Humans.Application.DTOs.TicketTransferRowDto
@{
    ViewData["Title"] = "Review transfer";
}

<h1>Review transfer</h1>

<dl class="row">
    <dt class="col-sm-3">From</dt><dd class="col-sm-9">@Model.RequesterDisplayName</dd>
    <dt class="col-sm-3">To</dt><dd class="col-sm-9">@Model.RecipientDisplayName <small class="text-muted">(@Model.RecipientEmail)</small></dd>
    <dt class="col-sm-3">Ticket</dt><dd class="col-sm-9">@Model.OriginalAttendeeName — @Model.TicketTypeName</dd>
    <dt class="col-sm-3">Reason</dt><dd class="col-sm-9">@Model.RequesterReason</dd>
    <dt class="col-sm-3">Requested</dt><dd class="col-sm-9">@Model.RequestedAt</dd>
</dl>

<form asp-action="Decide" method="post" class="mt-3">
    @Html.AntiForgeryToken()
    <input type="hidden" name="id" value="@Model.Id" />
    <div class="mb-3">
        <label for="adminNotes">Admin notes (optional)</label>
        <textarea id="adminNotes" name="adminNotes" rows="2" class="form-control" maxlength="1000"></textarea>
    </div>
    <button type="submit" name="approve" value="true" class="btn btn-success">Approve</button>
    <button type="submit" name="approve" value="false" class="btn btn-danger">Reject</button>
    <a class="btn btn-outline-secondary" asp-action="Index">Back</a>
</form>
```

- [ ] **Step 4: Build + manual smoke**

Confirm the policy name `TicketAdminOrAdmin` is the one already registered in DI — search `AddPolicy("TicketAdminOrAdmin"` in `Program.cs` or `AuthorizationPolicies.cs`. If it's a different name (e.g. `TicketAdminAndAdmin`), match that exactly.

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

Verify: as a non-admin user, GET `/Tickets/Transfers` → 403. As a TicketAdmin, queue lists pending requests, Approve/Reject decide them, redirect with TempData.

- [ ] **Step 5: Commit + push**

```bash
git add src/Humans.Web/Controllers/TicketTransferController.cs src/Humans.Web/Views/TicketTransfer/Index.cshtml src/Humans.Web/Views/TicketTransfer/Detail.cshtml
git commit -m "feat(tickets): admin review queue for transfers (#382)"
git push
```

---

### Task 7.2: Nav badge for pending transfers (TicketAdmin sidebar)

**Files:**
- Modify: `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs`
- Modify: `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs` (or wherever the Tickets section sidebar entry is rendered)

Pattern reference: read `NavBadgesViewComponent` and find an existing badge (e.g. pending feedback, pending applications). Mirror that.

- [ ] **Step 1: Add a `PendingTicketTransferCount` field to whichever model `NavBadgesViewComponent` returns**

In the view component, inject `ITicketTransferService` and call `_service.CountPendingAsync(ct)` inside the existing aggregator — only when the current user is a TicketAdmin / Admin. The existing class already gates fields by role; mirror that gating.

- [ ] **Step 2: Render the badge in the sidebar**

In whichever view (`AdminSidebarViewComponent` or `_AdminSidebar.cshtml`) renders the Tickets section's nav entries, add a child link "Transfer requests" pointing at `/Tickets/Transfers` and render the badge count.

- [ ] **Step 3: Build + manual smoke**

Verify badge increments when a buyer creates a request and clears when the admin decides.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/ViewComponents/
git commit -m "feat(tickets): nav badge for pending ticket transfers (#382)"
```

---

## Phase 8 — Tests, authorization, docs

### Task 8.1: Service tests — state machine + validation

**Files:**
- Create: `tests/Humans.Application.Tests/Tickets/TicketTransferServiceTests.cs`

Pattern reference: read an existing service test in `tests/Humans.Application.Tests/` (e.g. for `TicketSyncService` or `CampService`) for the Moq + xUnit + `IClock` stub style.

- [ ] **Step 1: Write tests covering each transition + validation rule**

The test class should cover at minimum:

- `LookupRecipient` — email exact match returns card; non-existent email returns null; ambiguous burner returns null; lookup excludes the requester themselves.
- `CreateRequest` — happy path returns row in Pending; cannot transfer to self; cannot transfer non-Valid ticket; cannot create when a Pending request already exists for the same attendee; cannot create when recipient already has a Valid ticket for the event; cannot transfer an attendee from someone else's order.
- `Cancel` — only requester can cancel; only Pending can cancel.
- `Reject` — only Pending can reject; sets DecidedByUserId/DecidedAt; writes audit.
- `Approve` (Phase 3 wiring covered) — vendor success → VendorResult.Succeeded + audit reflects success; vendor void-OK / issue-fail → VendorResult.VoidSucceededIssueFailed + audit reflects partial; vendor void-fail → VendorResult.Failed + audit reflects Option-C fallback.

Use `Mock<ITicketVendorService>` to drive the three vendor outcomes; use a `FakeClock` for deterministic timestamps; capture audit calls via `Mock<IAuditLogService>` and assert action enum + ActorUserId + EntityId.

(Code skeleton omitted for length — follow the pattern of an existing service test class. Each scenario is one `[Fact]`.)

- [ ] **Step 2: Run tests**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketTransferServiceTests"
```

Expected: all tests pass. Iterate the service if any reveal real bugs.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Tickets/TicketTransferServiceTests.cs
git commit -m "test(tickets): TicketTransferService state machine + validation (#382)"
```

---

### Task 8.2: Vendor + sync tests

**Files:**
- Create: `tests/Humans.Infrastructure.Tests/TicketTailorServiceWriteTests.cs`
- Create: `tests/Humans.Infrastructure.Tests/TicketSyncServiceNullOrderTests.cs`

- [ ] **Step 1: `TicketTailorServiceWriteTests`**

Use a `Mock<HttpMessageHandler>` (or `RichardSzalay.MockHttp` if already in the test project — search for usages) to intercept HTTP calls. Test:

- `VoidIssuedTicketAsync` POSTs `application/x-www-form-urlencoded` with `void_to_hold=true`/`false`, parses `id` + `hold_id` from the response.
- `IssueTicketAsync` POSTs form-encoded with `full_name`, `send_email`, plus either `hold_id` OR `event_id+ticket_type_id`; reads back the resulting `IssuedTicket` with `OrderId=null`.
- 400 → `TicketVendorWriteException(Validation)`; 401/403 → `AuthFailed`; 404 → `NotFound`; 429 → `RateLimited`; 5xx → `Transient`.

- [ ] **Step 2: `TicketSyncServiceNullOrderTests`**

Test scope: the new `null VendorOrderId` branch in `SyncOrdersAndAttendeesAsync`.

- Given a vendor returns one issued ticket with `VendorOrderId = null` and that ticket's `VendorTicketId` already exists locally with a parent `TicketOrderId`: the sync upserts the row (does not skip).
- Given a vendor returns one issued ticket with `VendorOrderId = null` and no local row exists: the sync logs and skips (current behaviour, just no longer blanket).
- Given a vendor returns one issued ticket with a known `VendorOrderId`: existing path still works.

- [ ] **Step 3: Run tests**

```bash
dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~TicketTailorServiceWriteTests | FullyQualifiedName~TicketSyncServiceNullOrderTests"
```

Expected: all tests pass.

- [ ] **Step 4: Commit + push**

```bash
git add tests/Humans.Infrastructure.Tests/
git commit -m "test(tickets): vendor write + sync null-order coverage (#382)"
git push
```

---

### Task 8.3: Authorization handler (defence in depth)

The buyer-flow controller already validates ownership server-side via `TicketTransferService.CreateRequestAsync` (it throws when `requesterUserId != attendee.TicketOrder.MatchedUserId`). The admin actions are gated by `TicketAdminOrAdmin`. So no separate `AuthorizationHandler` is strictly required — but the codebase prefers resource-based handlers (per `memory/code/authorization-pattern.md`-equivalent docs). For this PR we are **deliberately not** adding a handler: the service-layer guard is the source of truth, and the controller surface is small enough that adding a handler would be ceremony rather than defence.

This is a real call: if a future PR exposes a non-controller surface (a CLI, an internal API, etc.) we'll add the handler then. Document that decision in the section doc update (Task 8.4).

- [ ] **Step 1: No code change — just record the decision in the next task**

(Skip to Task 8.4.)

---

### Task 8.4: Update `docs/sections/Tickets.md`

**Files:**
- Modify: `docs/sections/Tickets.md`

- [ ] **Step 1: Add `TicketTransferRequest` to **Concepts** + **Data Model**

Insert the new entity description after `TicketSyncState`. Capture: lifecycle states; one Pending per attendee invariant; vendor result reflects writeback outcome; original attendee row goes to `Void` locally on approval (post-vendor).

- [ ] **Step 2: Update **Actors & Roles**

- Buyer: may request a transfer of any attendee on their own order. May cancel their Pending requests.
- TicketAdmin / Admin: may approve or reject pending transfers. (Board: explicitly cannot — add to Negative Access Rules.)

- [ ] **Step 3: Update **Invariants**

- Only Valid attendees can be transferred.
- Recipient must be a Humans user (resolved by exact-email or unique-burner-name lookup) — never free-text email.
- Recipient cannot already hold a Valid/CheckedIn ticket for the same vendor event.
- One Pending TicketTransferRequest per attendee at a time (enforced by partial unique index).
- On approval, the system attempts a TT void+reissue. On vendor failure the request still ends in Approved state; `VendorResult` records the failure and an admin must edit the ticket in the TT dashboard manually (Option C fallback).

- [ ] **Step 4: Update **Triggers**

- When a transfer is approved with vendor success: original `TicketAttendee` flips to `Void` locally; new `TicketAttendee` row is added with `VendorTicketId` from the TT issue response, `MatchedUserId` set to the recipient.
- When a transfer is approved with vendor failure: no local attendee changes; `VendorResult` records the failure; audit row reflects "Option-C fallback".
- Audit actions written: `TicketTransferRequested`, `TicketTransferCancelled`, `TicketTransferApproved`, `TicketTransferRejected`.

- [ ] **Step 5: Update **Cross-Section Dependencies**

- `IUserEmailService.GetUserIdByExactEmailAsync` — exact-email recipient lookup (Tickets-owned).
- `IProfileService.SearchByBurnerNameAsync` — burner-name wildcard recipient lookup (Profiles-owned, called by Tickets).
- Existing `IUserService.GetByIdAsync` and primary-email lookup are reused.

- [ ] **Step 6: Note the authorization decision**

Add a line under **Architecture** noting that ticket-transfer authorization is service-level (not a resource-based handler) — see Task 8.3 rationale.

- [ ] **Step 7: Build + commit**

```bash
git add docs/sections/Tickets.md
git commit -m "docs(tickets): document ticket transfer feature (#382)"
git push
```

---

### Task 8.5: Open the PR

- [ ] **Step 1: Verify all phases done**

Skim every task checkbox; confirm tests pass.

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

Expected: 0 build errors, 0 test failures.

- [ ] **Step 2: Open PR against `peterdrier/Humans` `main`**

Use the existing PR template / `gh pr create`. Title: `feat(tickets): ticket transfer between humans (#382)`. Body: link issue #382, link the probe doc, summarise the eight phases, list deliberate scope-cuts (no webhook subscription, no resource-based auth handler).

```bash
gh pr create --base main --head issue-382-ticket-transfer --title "feat(tickets): ticket transfer between humans (#382)" --body "$(cat <<'EOF'
## Summary
- Adds user-initiates → admin-approves ticket transfer flow per nobodies-collective/Humans#382
- TT writeback via void+reissue (Option B from probe); falls back to Humans-only (Option C) on vendor failure
- Homepage ticket card extended to list attendees when order has multiple, each with a Transfer button
- Admin review queue at /Tickets/Transfers gated by TicketAdminOrAdmin policy

## Test plan
- [ ] Buyer with 2+ tickets sees attendee list on dashboard
- [ ] Recipient lookup: full email matches, partial burner name matches uniquely or returns no result
- [ ] Baseball-card preview renders before submit; submission creates Pending request
- [ ] TicketAdmin approves → vendor void+reissue against stub → original goes Void, new attendee shows for recipient
- [ ] TicketAdmin rejects → audit row, no vendor calls
- [ ] Sync still ingests known-order tickets; no longer drops null-order tickets that exist locally

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review notes

The plan has been re-read against the issue spec. Coverage:

- **AC: Research TT API** — Phase 0 (DONE).
- **AC: `TicketTransferRequest` entity with state machine** — Tasks 1.1, 1.2, 1.4–1.6.
- **AC: Homepage ticket card lists attendee names + Transfer button** — Phase 5.
- **AC: Buyer-side request flow (lookup → confirm → submit)** — Phase 6.
- **AC: Recipient lookup supports email exact + burner wildcard, no email fragments** — Task 2.6 / Step 4 (the `if (trimmed.Contains('@'))` branch and `SearchByBurnerName` for the else branch).
- **AC: Baseball-card mini-profile before submission** — Task 6.1 / `Request.cshtml`.
- **AC: TicketAdmin review queue with approve/reject + notes** — Phase 7.
- **AC: On approval, TT attendee name matches new holder** — Task 3.4 (`IssueTicketAsync` with `request.RecipientDisplayName` as `FullName`).
- **AC: Both requester and recipient validated as Humans users** — service `CreateRequestAsync` (Task 2.6).
- **AC: All state transitions in audit log with both actors** — `_auditLog.WriteAsync` calls in each transition method.
- **AC: Sync does not revert/corrupt transferred tickets** — Phase 4.
- **AC: Gate list reflects post-transfer attendee** — covered by Task 3.4 pre-population (the new attendee with `MatchedUserId = recipient` exists locally before the next sync).
- **AC: Recipient's homepage card reflects new ticket after approval** — covered by Task 3.4 pre-population + the same homepage card population in Task 5.2 (the recipient's `GetAttendeesForBuyerAsync` won't return the new ticket since it joined to the original order's `MatchedUserId` = the requester. **This is a gap — see resolution below.**)

**Gap caught during self-review:** the homepage card in Task 5.2 enumerates attendees by `TicketOrder.MatchedUserId`. After approval, the recipient's new attendee row attaches to the original order whose `MatchedUserId` is still the requester. The recipient's card needs to enumerate by `TicketAttendee.MatchedUserId`, not by buyer.

**Resolution:** in Task 5.2, change `GetAttendeesForBuyerAsync` semantics — enumerate attendees where **either** `TicketOrder.MatchedUserId == userId` (the buyer's view of all their order's attendees) **or** `TicketAttendee.MatchedUserId == userId` and the attendee's parent order is not also in that set (the recipient's view of tickets transferred to them). Keep the union; deduplicate by attendee id. The "Transfer" button is gated by `CanRequestTransfer`, which already requires the user to be the buyer (the service enforces this), so a recipient seeing a transferred-in ticket cannot re-transfer it without first becoming its buyer — which they aren't.

**Action:** this fixes itself if `GetAttendeesForBuyerAsync` is renamed to `GetAttendeesVisibleToUserAsync` and the EF query becomes:

```csharp
return await _db.TicketAttendees
    .Include(a => a.TicketOrder)
    .Where(a => a.TicketOrder.MatchedUserId == userId || a.MatchedUserId == userId)
    .Distinct()
    .OrderBy(a => a.AttendeeName)
    .ToListAsync(ct);
```

And `CanRequestTransfer` in Task 5.2 becomes `a.Status == Valid && a.TicketOrder.MatchedUserId == userId && !pendingTransfers.ContainsKey(a.Id)` (i.e. only buyers can transfer).

Apply this rename + condition change inline in Task 5.2 when executing — no plan rework needed beyond this note.

**No other gaps identified.**
