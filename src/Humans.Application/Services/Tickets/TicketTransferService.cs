using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Owns the TicketTransferRequest aggregate's lifecycle. Sender initiates,
/// admin decides; on approval, attempts a TicketTailor void+reissue and falls
/// back to Option-C (Humans-only, admin must edit dashboard) on vendor failure.
/// </summary>
public sealed class TicketTransferService : ITicketTransferService
{
    private readonly ITicketTransferRepository _transferRepo;
    private readonly ITicketRepository _ticketRepo;
    private readonly ITicketVendorService _vendor;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLog;
    private readonly IClock _clock;
    private readonly ILogger<TicketTransferService> _logger;

    public TicketTransferService(
        ITicketTransferRepository transferRepo,
        ITicketRepository ticketRepo,
        ITicketVendorService vendor,
        ITicketQueryService ticketQueryService,
        IUserService userService,
        IUserEmailService userEmailService,
        IProfileService profileService,
        IAuditLogService auditLog,
        IClock clock,
        ILogger<TicketTransferService> logger)
    {
        _transferRepo = transferRepo;
        _ticketRepo = ticketRepo;
        _vendor = vendor;
        _ticketQueryService = ticketQueryService;
        _userService = userService;
        _userEmailService = userEmailService;
        _profileService = profileService;
        _auditLog = auditLog;
        _clock = clock;
        _logger = logger;
    }

    private const int MaxBurnerNameMatches = 10;

    public async Task<IReadOnlyList<ReceiverLookupResultDto>> LookupReceiversAsync(
        string query, Guid senderUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<ReceiverLookupResultDto>();
        var trimmed = query.Trim();

        // Email queries: exact match only, never fuzzy — don't leak addresses.
        if (trimmed.Contains('@'))
        {
            var userId = await _userEmailService.GetUserIdByExactEmailAsync(trimmed, ct);
            if (userId is null || userId == senderUserId)
                return Array.Empty<ReceiverLookupResultDto>();
            var card = await BuildReceiverCardAsync(userId.Value, ct);
            return card is null
                ? Array.Empty<ReceiverLookupResultDto>()
                : new[] { card };
        }

        // Name queries: case-insensitive contains over BurnerName + DisplayName
        // (the consolidated PersonSearchFields.Name bucket).
        var hits = await _profileService.SearchProfilesAsync(
            trimmed, PersonSearchFields.Name, MaxBurnerNameMatches, ct);
        var candidates = hits
            .Where(h => h.UserId != senderUserId)
            .ToList();

        var cards = new List<ReceiverLookupResultDto>(candidates.Count);
        foreach (var h in candidates)
        {
            var card = await BuildReceiverCardAsync(h.UserId, ct);
            if (card is not null) cards.Add(card);
        }
        return cards;
    }

    public async Task<ReceiverLookupResultDto?> GetReceiverCardAsync(
        Guid receiverUserId, Guid senderUserId, CancellationToken ct = default)
    {
        if (receiverUserId == senderUserId) return null;
        return await BuildReceiverCardAsync(receiverUserId, ct);
    }

    public async Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(
        Guid userId, CancellationToken ct = default)
    {
        var visible = await _ticketRepo.GetAttendeesVisibleToUserAsync(userId, ct);
        // GroupBy + First — defensive against any pre-existing duplicate pending rows
        // for the same attendee (CreateRequestAsync now blocks duplicates, but the
        // dashboard read should never crash if a stray duplicate exists).
        var pendingByAttendee = (await _transferRepo.GetBySenderAsync(userId, ct))
            .Where(r => r.Status == TicketTransferStatus.Pending)
            .GroupBy(r => r.OriginalTicketAttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        return visible
            .OrderBy(a => a.AttendeeName, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var pending = pendingByAttendee.TryGetValue(a.Id, out var transferId);
                return new MyAttendeeRowDto(
                    AttendeeId: a.Id,
                    AttendeeName: a.AttendeeName,
                    TicketTypeName: a.TicketTypeName,
                    CanSendTransfer: a.Status == TicketAttendeeStatus.Valid
                        && a.TicketOrder.MatchedUserId == userId
                        && !pending,
                    HasPendingOutgoingTransfer: pending,
                    PendingTransferRequestId: pending ? transferId : null);
            })
            .ToList();
    }

    public async Task<TicketTransferRowDto> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default)
    {
        if (dto.ReceiverUserId == senderUserId)
            throw new InvalidOperationException("Cannot transfer a ticket to yourself.");

        var attendee = await _ticketRepo.GetAttendeeByIdAsync(dto.OriginalAttendeeId, ct)
            ?? throw new InvalidOperationException("Attendee not found.");

        // Sender must own the parent order's MatchedUserId
        if (attendee.TicketOrder.MatchedUserId != senderUserId)
            throw new InvalidOperationException("You can only transfer tickets from your own orders.");

        if (attendee.Status != TicketAttendeeStatus.Valid)
            throw new InvalidOperationException("Only Valid tickets can be transferred.");

        var receiverUser = await _userService.GetByIdAsync(dto.ReceiverUserId, ct)
            ?? throw new InvalidOperationException("Receiver user not found.");
        var receiverProfile = await _profileService.GetProfileAsync(dto.ReceiverUserId, ct);
        // Defense-in-depth: BuildReceiverCardAsync filters suspended/unapproved at
        // the lookup layer, but Submit accepts a direct POST with a crafted
        // ReceiverUserId. Mirror the lookup behaviour (treat as not-found) so this
        // path can't bypass the security gate. Wording matches the not-found case
        // above so a tampered POST learns nothing about why the recipient was rejected.
        if (receiverProfile is not null && (receiverProfile.IsSuspended || !receiverProfile.IsApproved))
            throw new InvalidOperationException("Receiver user not found.");
        // No double-submits: refuse if there's already a pending transfer from this
        // sender for this attendee. ToDictionary in GetMyAttendeesAsync would crash
        // on duplicates, and the legitimate UX hides the Send button while pending.
        var existingPending = (await _transferRepo.GetBySenderAsync(senderUserId, ct))
            .Any(r => r.OriginalTicketAttendeeId == dto.OriginalAttendeeId
                && r.Status == TicketTransferStatus.Pending);
        if (existingPending)
            throw new InvalidOperationException("There is already a pending transfer request for this ticket.");
        var receiverLegalName = receiverProfile?.FullName is { Length: > 0 } legal
            ? legal
            : receiverUser.DisplayName;
        var receiverEmail = await _userEmailService.GetPrimaryEmailAsync(dto.ReceiverUserId, ct)
            ?? throw new InvalidOperationException("Receiver has no primary email on file.");

        var now = _clock.GetCurrentInstant();
        var request = new TicketTransferRequest
        {
            Id = Guid.NewGuid(),
            OriginalTicketAttendeeId = dto.OriginalAttendeeId,
            SenderUserId = senderUserId,
            ReceiverUserId = dto.ReceiverUserId,
            ReceiverLegalName = receiverLegalName,
            ReceiverEmail = receiverEmail,
            SenderReason = dto.Reason ?? string.Empty,
            Status = TicketTransferStatus.Pending,
            VendorResult = TicketTransferVendorResult.NotAttempted,
            RequestedAt = now,
        };

        await _transferRepo.AddAsync(request, ct);

        await _auditLog.LogAsync(
            AuditAction.TicketTransferRequested,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer requested: ticket {attendee.VendorTicketId} → {receiverLegalName}",
            senderUserId,
            dto.ReceiverUserId,
            nameof(User));

        return await BuildRowDtoAsync(request, ct);
    }

    public async Task CancelAsync(Guid transferRequestId, Guid senderUserId, CancellationToken ct = default)
    {
        var request = await _transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be cancelled.");
        if (request.SenderUserId != senderUserId)
            throw new InvalidOperationException("Only the Sender can cancel.");

        var now = _clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Cancelled;
        request.DecidedAt = now;
        await _transferRepo.UpdateAsync(request, ct);

        await _auditLog.LogAsync(
            AuditAction.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            request.Id,
            "Transfer cancelled by Sender",
            senderUserId);
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
            request.SenderUserId,
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
        try
        {
            await _transferRepo.UpdateAsync(request, ct);
        }
        catch (Exception ex) when (request.VendorResult is
            TicketTransferVendorResult.Succeeded or TicketTransferVendorResult.VoidSucceededIssueFailed)
        {
            // Vendor side committed but the local request row failed to persist.
            // Surface the partial state so an admin can reconcile manually — the new
            // TicketAttendee row may already exist (Succeeded) and the original is voided
            // at the vendor (both Succeeded and VoidSucceededIssueFailed paths).
            _logger.LogError(ex,
                "Transfer {TransferId} vendor write succeeded ({VendorResult}) but request UpdateAsync failed; manual reconcile required",
                request.Id, request.VendorResult);
            await _auditLog.LogAsync(
                AuditAction.TicketTransferApproved,
                nameof(TicketTransferRequest),
                request.Id,
                $"PARTIAL STATE: vendor writeback {request.VendorResult} but request commit failed: {ex.Message}",
                adminUserId,
                request.SenderUserId,
                nameof(User));
            throw;
        }

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
            request.SenderUserId,
            nameof(User));

        return await BuildRowDtoAsync(request, ct);
    }

    public async Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default)
    {
        var rows = (await _transferRepo.GetByStatusAsync(status, ct)).ToList();
        return await BuildRowDtosAsync(rows, ct);
    }

    public async Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(
        Guid userId, CancellationToken ct = default)
    {
        var rows = (await _transferRepo.GetBySenderAsync(userId, ct)).ToList();
        return await BuildRowDtosAsync(rows, ct);
    }

    public async Task<TicketTransferDetailDto?> GetDetailAsync(
        Guid transferRequestId, CancellationToken ct = default)
    {
        var request = await _transferRepo.GetByIdAsync(transferRequestId, ct);
        if (request is null) return null;

        var row = await BuildRowDtoAsync(request, ct);
        var senderCard = await BuildReceiverCardAsync(request.SenderUserId, ct);
        var receiverCard = await BuildReceiverCardAsync(request.ReceiverUserId, ct);

        // Cards fall back to a minimal stub if a profile somehow can't be built
        // (e.g. user soft-deleted between request and admin review). The row's
        // snapshot fields still carry the names we need.
        return new TicketTransferDetailDto(
            Row: row,
            SenderCard: senderCard ?? StubCard(request.SenderUserId, row.SenderDisplayName),
            ReceiverCard: receiverCard ?? StubCard(request.ReceiverUserId, row.ReceiverLegalName));
    }

    private static ReceiverLookupResultDto StubCard(Guid userId, string displayName) =>
        new(userId, displayName, BurnerName: null, PreferredEmail: null,
            HasCustomProfilePicture: false, ProfilePictureUrl: null);

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
            _logger.LogWarning(
                "TT void failed for transfer {TransferId} attendee {AttendeeId} ({Kind}); falling back to Option-C",
                request.Id, request.OriginalTicketAttendeeId, ex.Kind);
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
                FullName: request.ReceiverLegalName,
                Email: request.ReceiverEmail,
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

            // Vendor confirmed the void; mirror that locally so the Sender's
            // homepage card flips immediately. The reissue half failed — the
            // Receiver does not gain a new ticket here, but the original is
            // dead at the vendor and must read dead locally too.
            attendee.Status = TicketAttendeeStatus.Void;
            await _ticketRepo.UpsertAttendeeAsync(attendee, ct);
            _ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, receiverUserId: null);
            return;
        }

        // Sub-step 3: atomically write both attendee rows (new Receiver Valid + original
        // flipped to Void) in one DbContext + one SaveChangesAsync, so a mid-batch failure
        // can't leave the DB briefly showing both Sender and Receiver holding a Valid
        // ticket while the vendor has already committed the void+reissue.
        var now = _clock.GetCurrentInstant();
        attendee.Status = TicketAttendeeStatus.Void;
        await _ticketRepo.UpsertAttendeesAsync(new[]
        {
            new TicketAttendee
            {
                Id = Guid.NewGuid(),
                VendorTicketId = issued.VendorTicketId,
                TicketOrderId = attendee.TicketOrderId, // attach to the original order locally
                AttendeeName = request.ReceiverLegalName,
                AttendeeEmail = request.ReceiverEmail,
                TicketTypeName = attendee.TicketTypeName,
                Price = attendee.Price, // local snapshot — TT may rebill differently, see probe Open Questions
                Status = TicketAttendeeStatus.Valid,
                VendorEventId = attendee.VendorEventId,
                SyncedAt = now,
                MatchedUserId = request.ReceiverUserId,
            },
            attendee,
        }, ct);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = voidResult.HoldId is null ? null : $"hold {voidResult.HoldId}";

        _ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);
    }

    private async Task<ReceiverLookupResultDto?> BuildReceiverCardAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null) return null;
        var profile = await _profileService.GetProfileAsync(userId, ct);
        // Security gate: filter suspended or unapproved profiles at the lookup layer
        // (per docs/features/42-ticket-transfer.md — Receiver Lookup Contract). Gates
        // both lookup paths (exact-email + burner-name) and the detail-fetch used by
        // the confirm-card render.
        if (profile is not null && (profile.IsSuspended || !profile.IsApproved))
            return null;
        var primary = await _userEmailService.GetPrimaryEmailAsync(userId, ct);
        return new ReceiverLookupResultDto(
            UserId: userId,
            DisplayName: user.DisplayName,
            BurnerName: profile?.BurnerName,
            PreferredEmail: primary,
            HasCustomProfilePicture: profile?.HasCustomProfilePicture ?? false,
            ProfilePictureUrl: user.ProfilePictureUrl);
    }

    private async Task<TicketTransferRowDto> BuildRowDtoAsync(TicketTransferRequest r, CancellationToken ct)
    {
        var users = await _userService.GetByIdsAsync(
            r.DecidedByUserId is null
                ? new[] { r.SenderUserId }
                : new[] { r.SenderUserId, r.DecidedByUserId.Value },
            ct);
        return BuildRowDto(r, users, await ResolveAttendeeAsync(r, ct));
    }

    private async Task<IReadOnlyList<TicketTransferRowDto>> BuildRowDtosAsync(
        IReadOnlyList<TicketTransferRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<TicketTransferRowDto>();

        var userIds = new HashSet<Guid>();
        foreach (var r in rows)
        {
            userIds.Add(r.SenderUserId);
            if (r.DecidedByUserId is { } decider) userIds.Add(decider);
        }
        var users = await _userService.GetByIdsAsync(userIds, ct);

        var result = new List<TicketTransferRowDto>(rows.Count);
        foreach (var r in rows)
            result.Add(BuildRowDto(r, users, await ResolveAttendeeAsync(r, ct)));
        return result;
    }

    private async Task<TicketAttendee> ResolveAttendeeAsync(
        TicketTransferRequest r, CancellationToken ct) =>
        r.OriginalTicketAttendee
            ?? await _ticketRepo.GetAttendeeByIdAsync(r.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

    private static TicketTransferRowDto BuildRowDto(
        TicketTransferRequest r,
        IReadOnlyDictionary<Guid, User> users,
        TicketAttendee attendee)
    {
        users.TryGetValue(r.SenderUserId, out var sender);
        User? decider = null;
        if (r.DecidedByUserId is { } deciderId) users.TryGetValue(deciderId, out decider);

        return new TicketTransferRowDto(
            Id: r.Id,
            OriginalAttendeeId: r.OriginalTicketAttendeeId,
            OriginalAttendeeName: attendee.AttendeeName,
            TicketTypeName: attendee.TicketTypeName,
            OriginalAttendeeStatus: attendee.Status,
            SenderUserId: r.SenderUserId,
            SenderDisplayName: sender?.DisplayName ?? "(unknown)",
            ReceiverUserId: r.ReceiverUserId,
            ReceiverLegalName: r.ReceiverLegalName,
            ReceiverEmail: r.ReceiverEmail,
            SenderReason: r.SenderReason,
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
