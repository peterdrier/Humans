using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
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

        // Requester must own the parent order's MatchedUserId
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

        await _auditLog.LogAsync(
            AuditAction.TicketTransferRequested,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer requested: ticket {attendee.VendorTicketId} → {recipientUser.DisplayName}",
            requesterUserId,
            dto.RecipientUserId,
            nameof(User));

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

        await _auditLog.LogAsync(
            AuditAction.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            request.Id,
            "Transfer cancelled by requester",
            requesterUserId);
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

        await _auditLog.LogAsync(
            AuditAction.TicketTransferRejected,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer rejected{(string.IsNullOrEmpty(adminNotes) ? "" : ": " + adminNotes)}",
            adminUserId,
            request.RequesterUserId,
            nameof(User));

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

        await _auditLog.LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            request.Id,
            request.VendorResult switch
            {
                TicketTransferVendorResult.Succeeded =>
                    $"Transfer approved (TT void+reissue OK, new ticket {request.NewVendorTicketId})",
                TicketTransferVendorResult.VoidSucceededIssueFailed =>
                    $"Transfer approved (TT void OK, reissue FAILED: {request.VendorMessage}) — manual reissue needed",
                TicketTransferVendorResult.Failed =>
                    $"Transfer approved (TT writeback FAILED: {request.VendorMessage}) — Option-C fallback, edit ticket in TT dashboard",
                _ => "Transfer approved"
            },
            adminUserId,
            request.RequesterUserId,
            nameof(User));

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

    private async Task<RecipientLookupResultDto?> BuildRecipientCardAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null) return null;
        var profile = await _profileService.GetProfileAsync(userId, ct);
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
