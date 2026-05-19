using System.Text.Json;
using Humans.Application.DTOs;
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
using NodaTime.Serialization.SystemTextJson;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Owns the TicketTransferRequest aggregate's lifecycle. Sender initiates,
/// admin decides; on approval, attempts a TicketTailor void+reissue and falls
/// back to Option-C (Humans-only, admin must edit dashboard) on vendor failure.
/// </summary>
public sealed class TicketTransferService(
    ITicketTransferRepository transferRepo,
    ITicketRepository ticketRepo,
    ITicketVendorService vendor,
    ITicketQueryService ticketQueryService,
    IUserService userService,
    IUserEmailService userEmailService,
    IProfileService profileService,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<TicketTransferService> logger) : ITicketTransferService
{
    private readonly IProfileService _profileService = profileService;

    private const int MaxBurnerNameMatches = 10;

    private static readonly JsonSerializerOptions VendorStepsJsonOptions =
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    private static void AppendStep(TicketTransferRequest request, TicketTransferVendorStep step)
    {
        var list = JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(
            request.VendorStepsJson, VendorStepsJsonOptions) ?? [];
        list.Add(step);
        request.VendorStepsJson = JsonSerializer.Serialize(list, VendorStepsJsonOptions);
    }

    // Diverges from /api/profiles/search to support exact-email lookup. See memory/architecture/person-search.md.
    public async Task<IReadOnlyList<ReceiverLookupResultDto>> LookupReceiversAsync(
        string query, Guid senderUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var trimmed = query.Trim();

        // Email: exact match only — don't leak addresses.
        if (trimmed.Contains('@'))
        {
            var userId = await userEmailService.GetUserIdByExactEmailAsync(trimmed, ct);
            if (userId is null || userId == senderUserId)
                return [];
            var card = await BuildReceiverCardAsync(userId.Value, ct);
            return card is null
                ? Array.Empty<ReceiverLookupResultDto>()
                : new[] { card };
        }

        var hits = await userService.SearchUsersAsync(
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
        var visible = await ticketRepo.GetAttendeesVisibleToUserAsync(userId, ct);
        // GroupBy defensive: stray duplicate pendings must not crash dashboard read.
        var pendingByAttendee = (await transferRepo.GetBySenderAsync(userId, ct))
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
                        && TicketAttendeeOwnership.IsCurrentOwner(a, userId)
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

        var attendee = await ticketRepo.GetAttendeeByIdAsync(dto.OriginalAttendeeId, ct)
            ?? throw new InvalidOperationException("Attendee not found.");

        if (!TicketAttendeeOwnership.IsCurrentOwner(attendee, senderUserId))
            throw new InvalidOperationException("You can only transfer tickets you currently hold.");

        if (attendee.Status != TicketAttendeeStatus.Valid)
            throw new InvalidOperationException("Only Valid tickets can be transferred.");

        var receiverInfo = await userService.GetUserInfoAsync(dto.ReceiverUserId, ct)
            ?? throw new InvalidOperationException("Receiver user not found.");
        // Defense-in-depth: receiver MUST have legal name; mirror not-found message to avoid leaking why.
        if (!receiverInfo.HasRequiredNameFields)
            throw new InvalidOperationException("Receiver user not found.");
        var receiverProfile = receiverInfo.Profile!;
        // Block duplicate pendings (UX hides Send; ToDictionary would crash on dupes).
        var existingPending = (await transferRepo.GetBySenderAsync(senderUserId, ct))
            .Any(r => r.OriginalTicketAttendeeId == dto.OriginalAttendeeId
                && r.Status == TicketTransferStatus.Pending);
        if (existingPending)
            throw new InvalidOperationException("There is already a pending transfer request for this ticket.");
        var receiverLegalName = receiverProfile.FullName;
        var receiverEmail = await userEmailService.GetPrimaryEmailAsync(dto.ReceiverUserId, ct)
            ?? throw new InvalidOperationException("Receiver has no primary email on file.");

        var now = clock.GetCurrentInstant();
        var request = new TicketTransferRequest
        {
            Id = Guid.NewGuid(),
            OriginalTicketAttendeeId = dto.OriginalAttendeeId,
            SenderUserId = senderUserId,
            ReceiverUserId = dto.ReceiverUserId,
            ReceiverLegalName = receiverLegalName,
            ReceiverEmail = receiverEmail,
            SenderReason = dto.Reason,
            Status = TicketTransferStatus.Pending,
            VendorResult = TicketTransferVendorResult.NotAttempted,
            RequestedAt = now,
        };

        await transferRepo.AddAsync(request, ct);

        await auditLog.LogAsync(
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
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be cancelled.");
        if (request.SenderUserId != senderUserId)
            throw new InvalidOperationException("Only the Sender can cancel.");

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Cancelled;
        request.DecidedAt = now;
        await transferRepo.UpdateAsync(request, ct);

        await auditLog.LogAsync(
            AuditAction.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            request.Id,
            "Transfer cancelled by Sender",
            senderUserId);
    }

    public async Task<TicketTransferRowDto> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Rejected;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = adminNotes;
        await transferRepo.UpdateAsync(request, ct);

        await auditLog.LogAsync(
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
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");

        var now = clock.GetCurrentInstant();

        // Option B: vendor writeback; falls back to Option C on failure.
        await WriteToVendorAsync(request, ct);

        request.Status = TicketTransferStatus.Approved;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = adminNotes;
        try
        {
            await transferRepo.UpdateAsync(request, ct);
        }
        catch (Exception ex) when (request.VendorResult is
            TicketTransferVendorResult.Succeeded or TicketTransferVendorResult.VoidSucceededIssueFailed)
        {
            // Vendor committed but local persist failed — surface for manual reconcile.
            logger.LogError(ex,
                "Transfer {TransferId} vendor write succeeded ({VendorResult}) but request UpdateAsync failed; manual reconcile required",
                request.Id, request.VendorResult);
            await auditLog.LogAsync(
                AuditAction.TicketTransferApproved,
                nameof(TicketTransferRequest),
                request.Id,
                $"PARTIAL STATE: vendor writeback {request.VendorResult} but request commit failed: {ex.Message}",
                adminUserId,
                request.SenderUserId,
                nameof(User));
            throw;
        }

        await auditLog.LogAsync(
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
        var rows = (await transferRepo.GetByStatusAsync(status, ct)).ToList();
        return await BuildRowDtosAsync(rows, ct);
    }

    public async Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(
        Guid userId, CancellationToken ct = default)
    {
        var rows = (await transferRepo.GetBySenderAsync(userId, ct)).ToList();
        return await BuildRowDtosAsync(rows, ct);
    }

    public async Task<TicketTransferDetailDto?> GetDetailAsync(
        Guid transferRequestId, CancellationToken ct = default)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct);
        if (request is null) return null;

        var row = await BuildRowDtoAsync(request, ct);
        var senderCard = await BuildReceiverCardAsync(request.SenderUserId, ct);
        var receiverCard = await BuildReceiverCardAsync(request.ReceiverUserId, ct);

        var attendee = await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct);

        var order = attendee?.TicketOrder;
        var siblingIds = order is not null
            ? (await ticketRepo.GetVendorTicketIdsForOrderAsync(order.Id, ct))
                .OrderBy(s => s, StringComparer.Ordinal).ToList()
            : (IReadOnlyList<string>)[];

        // Stub cards if profile build fails (e.g. user soft-deleted); snapshot carries names.
        return new TicketTransferDetailDto(
            Row: row,
            SenderCard: senderCard ?? StubCard(request.SenderUserId, row.SenderDisplayName),
            ReceiverCard: receiverCard ?? StubCard(request.ReceiverUserId, row.ReceiverLegalName),
            OrderDashboardUrl: order?.VendorDashboardUrl,
            VendorStepsJson: request.VendorStepsJson,
            OriginalAttendeeVendorTicketId: attendee?.VendorTicketId ?? string.Empty,
            OriginalAttendeeEmail: attendee?.AttendeeEmail,
            OrderVendorId: order?.VendorOrderId ?? string.Empty,
            OrderPurchasedAt: order?.PurchasedAt ?? Instant.MinValue,
            OrderBuyerEmail: order?.BuyerEmail ?? string.Empty,
            SiblingVendorTicketIds: siblingIds);
    }

    private static ReceiverLookupResultDto StubCard(Guid userId, string displayName) =>
        new(userId, displayName, BurnerName: null, PreferredEmail: null,
            HasCustomProfilePicture: false, ProfilePictureUrl: null);

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        transferRepo.CountPendingAsync(ct);

    public async Task<TicketTransferRowDto> RetryIssueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Approved
            || request.VendorResult != TicketTransferVendorResult.VoidSucceededIssueFailed)
        {
            throw new InvalidOperationException(
                "Retry is only allowed when the transfer is Approved with VendorResult=VoidSucceededIssueFailed.");
        }

        var steps = JsonSerializer.Deserialize<List<TicketTransferVendorStep>>(
            request.VendorStepsJson, VendorStepsJsonOptions) ?? [];
        var lastVoid = steps.LastOrDefault(s =>
            s.Kind == TicketTransferVendorStepKind.Void && s.Success && s.VendorReferenceId is not null);
        if (lastVoid is null)
            throw new InvalidOperationException("No recorded hold id to retry against.");

        var attendee = await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

        var now = clock.GetCurrentInstant();
        VendorTicketDto issued;
        try
        {
            issued = await vendor.IssueTicketAsync(new IssueTicketRequest(
                EventId: null,
                TicketTypeId: null,
                HoldId: lastVoid.VendorReferenceId,
                FullName: request.ReceiverLegalName,
                Email: request.ReceiverEmail,
                SendEmail: true,
                ExternalReference: request.Id.ToString("N")), ct);
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
            await transferRepo.UpdateAsync(request, ct);
            await auditLog.LogAsync(
                AuditAction.TicketTransferApproved,
                nameof(TicketTransferRequest),
                request.Id,
                $"Retry-issue failed: {ex.Message}",
                adminUserId,
                request.SenderUserId,
                nameof(User));
            return await BuildRowDtoAsync(request, ct);
        }

        // Vendor issued — any local failure beyond here must be audited (hold is consumed).
        try
        {
            await ticketRepo.UpsertAttendeeAsync(new TicketAttendee
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

            await transferRepo.UpdateAsync(request, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "PARTIAL STATE: retry-issue succeeded at vendor (ticket {NewVendorTicketId}, hold {HoldId}) but local writeback failed for transfer {TransferId}. Manual reconciliation required.",
                issued.VendorTicketId, lastVoid.VendorReferenceId, request.Id);
            await auditLog.LogAsync(
                AuditAction.TicketTransferApproved,
                nameof(TicketTransferRequest),
                request.Id,
                $"PARTIAL STATE: retry-issue produced vendor ticket {issued.VendorTicketId} but local update failed: {ex.Message}",
                adminUserId,
                request.SenderUserId,
                nameof(User));
            throw;
        }

        ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);

        await auditLog.LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            request.Id,
            $"Retry-issue success: ticket {issued.VendorTicketId}",
            adminUserId,
            request.SenderUserId,
            nameof(User));

        return await BuildRowDtoAsync(request, ct);
    }

    private async Task WriteToVendorAsync(TicketTransferRequest request, CancellationToken ct)
    {
        var attendee = request.OriginalTicketAttendee
            ?? await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing during vendor writeback.");

        // 1: void to hold (prevents sold-out race on reissue).
        VoidIssuedTicketResult voidResult;
        try
        {
            voidResult = await vendor.VoidIssuedTicketAsync(
                attendee.VendorTicketId, voidToHold: true, ct);
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Void,
                Success: true,
                OccurredAt: clock.GetCurrentInstant(),
                VendorReferenceId: voidResult.HoldId,
                RequestSummary: $"void {attendee.VendorTicketId}",
                ResponseSummary: $"hold {voidResult.HoldId}",
                ErrorMessage: null));
        }
        catch (TicketVendorWriteException ex)
        {
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Void,
                Success: false,
                OccurredAt: clock.GetCurrentInstant(),
                VendorReferenceId: null,
                RequestSummary: $"void {attendee.VendorTicketId}",
                ResponseSummary: null,
                ErrorMessage: ex.Message));
            request.VendorResult = TicketTransferVendorResult.Failed;
            request.VendorMessage = $"Void failed ({ex.Kind}): {ex.Message}";
            logger.LogWarning(
                "TT void failed for transfer {TransferId} attendee {AttendeeId} ({Kind}); falling back to Option-C",
                request.Id, request.OriginalTicketAttendeeId, ex.Kind);
            return;
        }

        // 2: issue replacement against hold.
        VendorTicketDto issued;
        try
        {
            issued = await vendor.IssueTicketAsync(new IssueTicketRequest(
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
                OccurredAt: clock.GetCurrentInstant(),
                VendorReferenceId: issued.VendorTicketId,
                RequestSummary: $"issue for {request.ReceiverEmail} hold {voidResult.HoldId}",
                ResponseSummary: $"ticket {issued.VendorTicketId}",
                ErrorMessage: null));
        }
        catch (TicketVendorWriteException ex)
        {
            AppendStep(request, new TicketTransferVendorStep(
                Kind: TicketTransferVendorStepKind.Issue,
                Success: false,
                OccurredAt: clock.GetCurrentInstant(),
                VendorReferenceId: null,
                RequestSummary: $"issue for {request.ReceiverEmail} hold {voidResult.HoldId}",
                ResponseSummary: null,
                ErrorMessage: ex.Message));
            request.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
            request.VendorMessage = $"Issue failed ({ex.Kind}): {ex.Message} (hold {voidResult.HoldId})";
            logger.LogError(ex,
                "TT issue failed for transfer {TransferId} after successful void; hold {HoldId} retained",
                request.Id, voidResult.HoldId);

            // Void succeeded at vendor; mirror locally so Sender's card flips. Reissue failed.
            attendee.Status = TicketAttendeeStatus.Void;
            await ticketRepo.UpsertAttendeeAsync(attendee, ct);
            ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, receiverUserId: null);
            return;
        }

        // 3: write both attendee rows atomically (one SaveChanges) so no Valid-on-both window.
        var now = clock.GetCurrentInstant();
        attendee.Status = TicketAttendeeStatus.Void;
        await ticketRepo.UpsertAttendeesAsync([
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
            attendee
        ], ct);
        AppendStep(request, new TicketTransferVendorStep(
            Kind: TicketTransferVendorStepKind.LocalWriteback,
            Success: true,
            OccurredAt: clock.GetCurrentInstant(),
            VendorReferenceId: null,
            RequestSummary: $"upsert void {attendee.VendorTicketId} + new {issued.VendorTicketId}",
            ResponseSummary: null,
            ErrorMessage: null));

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = voidResult.HoldId is null ? null : $"hold {voidResult.HoldId}";

        ticketQueryService.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);
    }

    private async Task<ReceiverLookupResultDto?> BuildReceiverCardAsync(Guid userId, CancellationToken ct)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        // Receiver Lookup Contract: legal name required. See docs/features/tickets/ticket-transfer.md.
        if (info is null || !info.HasRequiredNameFields) return null;
        var profile = info.Profile!;
        var primary = await userEmailService.GetPrimaryEmailAsync(userId, ct);
        return new ReceiverLookupResultDto(
            UserId: userId,
            DisplayName: info.BurnerName,
            BurnerName: profile.BurnerName,
            PreferredEmail: primary,
            HasCustomProfilePicture: profile.HasCustomPicture,
            ProfilePictureUrl: info.ProfilePictureUrl);
    }

    private async Task<TicketTransferRowDto> BuildRowDtoAsync(TicketTransferRequest r, CancellationToken ct)
    {
        var users = await userService.GetUserInfosAsync(
            r.DecidedByUserId is null
                ? new[] { r.SenderUserId }
                : new[] { r.SenderUserId, r.DecidedByUserId.Value },
            ct);
        return BuildRowDto(r, users, await ResolveAttendeeAsync(r, ct));
    }

    private async Task<IReadOnlyList<TicketTransferRowDto>> BuildRowDtosAsync(
        IReadOnlyList<TicketTransferRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var userIds = new HashSet<Guid>();
        foreach (var r in rows)
        {
            userIds.Add(r.SenderUserId);
            if (r.DecidedByUserId is { } decider) userIds.Add(decider);
        }
        var users = await userService.GetUserInfosAsync(userIds, ct);

        var result = new List<TicketTransferRowDto>(rows.Count);
        foreach (var r in rows)
            result.Add(BuildRowDto(r, users, await ResolveAttendeeAsync(r, ct)));
        return result;
    }

    private async Task<TicketAttendee> ResolveAttendeeAsync(
        TicketTransferRequest r, CancellationToken ct) =>
        r.OriginalTicketAttendee
            ?? await ticketRepo.GetAttendeeByIdAsync(r.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

    private static TicketTransferRowDto BuildRowDto(
        TicketTransferRequest r,
        IReadOnlyDictionary<Guid, UserInfo> users,
        TicketAttendee attendee)
    {
        users.TryGetValue(r.SenderUserId, out var sender);
        UserInfo? decider = null;
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
