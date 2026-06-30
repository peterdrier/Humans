using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Owns the TicketTransferRequest aggregate's lifecycle. The Sender initiates a
/// request; the ticket team either runs the automated TicketTailor void(-to-hold)+reissue
/// (<see cref="ProcessTransferAsync"/>) or records a manual outcome
/// (<see cref="ApproveAsync"/> = "mark successful", <see cref="RejectAsync"/> =
/// "cancel with reason"). The next ticket sync reconciles local attendee rows. Requests
/// email the Sender + the ticket team; decisions email the Sender + Receiver.
/// </summary>
public sealed class TicketTransferService(
    ITicketTransferRepository transferRepo,
    ITicketRepository ticketRepo,
    ITicketVendorService vendor,
    IUserServiceRead userService,
    IUserEmailService userEmailService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLog,
    ITicketCacheInvalidator cacheInvalidator,
    IClock clock,
    ILogger<TicketTransferService> logger) : ITicketTransferService
{

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
                var owner = TicketAttendeeOwnership.IsCurrentOwner(a, userId);
                return new MyAttendeeRowDto(
                    AttendeeId: a.Id,
                    AttendeeName: a.AttendeeName,
                    AttendeeEmail: a.AttendeeEmail,
                    VendorTicketId: a.VendorTicketId,
                    TicketTypeName: a.TicketTypeName,
                    Status: a.Status,
                    IsCurrentOwner: owner,
                    CanSendTransfer: a.Status == TicketAttendeeStatus.Valid && a.CheckedInAt is null && owner && !pending,
                    HasPendingOutgoingTransfer: pending,
                    PendingTransferRequestId: pending ? transferId : null);
            })
            .ToList();
    }

    public async Task<TicketTransferConfirmDto?> GetConfirmationAsync(
        Guid attendeeId, Guid receiverUserId, Guid senderUserId, CancellationToken ct = default)
    {
        if (receiverUserId == senderUserId) return null;

        var attendee = await ticketRepo.GetAttendeeByIdAsync(attendeeId, ct);
        if (attendee is null
            || attendee.Status != TicketAttendeeStatus.Valid
            || attendee.CheckedInAt is not null
            || !TicketAttendeeOwnership.IsCurrentOwner(attendee, senderUserId))
        {
            return null;
        }

        var receiverInfo = await userService.GetUserInfoAsync(receiverUserId, ct);
        if (receiverInfo is null || !receiverInfo.HasRequiredNameFields) return null;
        var receiverEmail = await userEmailService.GetPrimaryEmailAsync(receiverUserId, ct);
        if (string.IsNullOrWhiteSpace(receiverEmail)) return null;

        return new TicketTransferConfirmDto(
            AttendeeId: attendee.Id,
            AttendeeName: attendee.AttendeeName,
            VendorTicketId: attendee.VendorTicketId,
            ReceiverUserId: receiverUserId,
            ReceiverLegalName: receiverInfo.Profile!.FullName,
            ReceiverEmail: receiverEmail);
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

        // A gate scan keeps Status = Valid and records the scan in CheckedInAt
        // (nobodies-collective/Humans#736), so the Valid check above no longer
        // catches an already-used ticket — guard on CheckedInAt explicitly.
        if (attendee.CheckedInAt is not null)
            throw new InvalidOperationException("Checked-in tickets cannot be transferred.");

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
            RequestedAt = now,
        };

        await transferRepo.AddAsync(request, ct);

        // Pendency is baked into the cached holdings rows ("transfer pending" stamp),
        // so every pendency change must evict the sender's holdings.
        cacheInvalidator.InvalidateAfterTransfer(senderUserId, dto.ReceiverUserId);

        await auditLog.LogAsync(
            AuditAction.TicketTransferRequested,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer requested: ticket {attendee.VendorTicketId} → {receiverLegalName}",
            senderUserId,
            dto.ReceiverUserId,
            nameof(User));

        await NotifyRequestedAsync(request, attendee, senderUserId, ct);

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
        EnsureNotMidProcessing(request);

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Cancelled;
        request.DecidedAt = now;
        await transferRepo.UpdateAsync(request, ct);

        cacheInvalidator.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);

        await auditLog.LogAsync(
            AuditAction.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            request.Id,
            "Transfer cancelled by Sender",
            senderUserId);
    }

    public async Task<TicketTransferRowDto> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await LoadPendingAsync(transferRequestId, ct);
        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            "Transfer marked successful (processed manually in TicketTailor)", ct);
        return await BuildRowDtoAsync(request, ct);
    }

    public async Task<TicketTransferRowDto> ProcessTransferAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await LoadPendingAsync(transferRequestId, ct);
        // A partial (already-voided) request must not be re-processed — that would void the
        // already-voided ticket again and overwrite the partial state. Finish + Mark successful.
        EnsureNotMidProcessing(request);

        // Attempt the void(-to-hold)+reissue. On vendor failure this records the outcome on
        // the request but does NOT throw — we decide what to do with the result below.
        await WriteToVendorAsync(request, ct);

        // Once a vendor void has committed it is irreversible — the recorded outcome must persist
        // regardless of whether the admin's HTTP request was aborted, or TT and Humans diverge.
        var persistCt = request.VendorResult is TicketTransferVendorResult.Succeeded
            or TicketTransferVendorResult.VoidSucceededIssueFailed
            ? CancellationToken.None
            : ct;

        if (request.VendorResult != TicketTransferVendorResult.Succeeded)
        {
            // Not transferred: persist the diagnostic (and any local void already mirrored),
            // audit it, and leave the request Pending so the team can finish in TicketTailor
            // and then fall back to ApproveAsync ("Mark successful"). No success emails.
            await transferRepo.UpdateAsync(request, persistCt);
            await auditLog.LogAsync(
                AuditAction.TicketTransferAutoFailed,
                nameof(TicketTransferRequest),
                request.Id,
                request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed
                    ? $"Automated process PARTIAL — {request.VendorMessage}; finish reissue in TicketTailor, then Mark successful"
                    : $"Automated process FAILED — {request.VendorMessage}; process manually in TicketTailor",
                adminUserId,
                request.SenderUserId,
                nameof(User));

            throw new InvalidOperationException(
                request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed
                    ? $"Ticket was voided but the reissue failed ({request.VendorMessage}). Finish the reissue in TicketTailor, then use “Mark successful”."
                    : $"Automated void+reissue failed ({request.VendorMessage}). Process this transfer manually in TicketTailor, then use “Mark successful”.");
        }

        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            $"Transfer processed automatically (TT void+reissue OK, new ticket {request.NewVendorTicketId})", persistCt);
        return await BuildRowDtoAsync(request, ct);
    }

    public async Task<TicketTransferRowDto> RetryReissueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending
            || request.VendorResult != TicketTransferVendorResult.VoidSucceededIssueFailed)
        {
            throw new InvalidOperationException(
                "Retry is only available for a part-processed transfer (ticket voided, reissue pending).");
        }
        if (string.IsNullOrEmpty(request.VendorHoldId))
        {
            throw new InvalidOperationException(
                "No hold id was recorded for this transfer — finish the reissue in TicketTailor and use “Mark successful”.");
        }

        var attendee = await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

        // Reissue from the held seat (same ticket type). The original is already Void locally.
        VendorTicketDto issued;
        try
        {
            issued = await vendor.IssueTicketAsync(new IssueTicketRequest(
                EventId: null,
                TicketTypeId: null,
                HoldId: request.VendorHoldId,
                FullName: request.ReceiverLegalName,
                Email: request.ReceiverEmail,
                SendEmail: true,
                ExternalReference: request.Id.ToString("N")), ct);
        }
        catch (Exception ex)
        {
            // Still failing — stay in the partial state, refresh the diagnostic, keep the hold so
            // the admin can retry again (or finish in TicketTailor). Persist with a non-request
            // token so an aborted retry doesn't lose the updated diagnostic.
            request.VendorMessage =
                $"Reissue retry failed ({VendorFailureDetail(ex)}) — retry again or finish in TicketTailor against hold {request.VendorHoldId}";
            await transferRepo.UpdateAsync(request, CancellationToken.None);
            await auditLog.LogAsync(
                AuditAction.TicketTransferAutoFailed,
                nameof(TicketTransferRequest),
                request.Id,
                $"Reissue retry failed — {VendorFailureDetail(ex)}",
                adminUserId,
                request.SenderUserId,
                nameof(User));
            logger.LogError(ex, "Reissue retry failed for transfer {TransferId} against hold {HoldId}",
                request.Id, request.VendorHoldId);
            throw new InvalidOperationException(
                $"Reissue retry failed ({VendorFailureDetail(ex)}). Try again, or finish in TicketTailor and use “Mark successful”.");
        }

        // Reissue committed — the new ticket exists at TT, so the local write must complete even if
        // the admin request was aborted. Add the new Valid row (original already Void).
        var now = clock.GetCurrentInstant();
        await ticketRepo.UpsertAttendeesAsync(
            [BuildReissuedAttendee(attendee, request, issued.VendorTicketId, now)], CancellationToken.None);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = $"hold {request.VendorHoldId} (retried)";
        request.VendorHoldId = null; // consumed
        cacheInvalidator.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);

        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            $"Reissue retried OK (new ticket {issued.VendorTicketId})", CancellationToken.None);
        return await BuildRowDtoAsync(request, ct);
    }

    private async Task<TicketTransferRequest> LoadPendingAsync(Guid transferRequestId, CancellationToken ct)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");
        return request;
    }

    // A VoidSucceededIssueFailed request has already had its ticket voided at the vendor — that
    // void is irreversible. Cancel/Reject would drop it from the queue and strand the receiver
    // (seat gone, no replacement); the only forward path is to finish the reissue in TicketTailor
    // and then "Mark successful" (ApproveAsync).
    private static void EnsureNotMidProcessing(TicketTransferRequest request)
    {
        if (request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed)
            throw new InvalidOperationException(
                "This transfer is mid-processing — the ticket was already voided and is awaiting reissue. Finish it in TicketTailor, then use “Mark successful”.");
    }

    private async Task MarkApprovedAsync(
        TicketTransferRequest request, Guid adminUserId, string? adminNotes,
        string auditDescription, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Approved;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = adminNotes;
        await transferRepo.UpdateAsync(request, ct);

        // Approving changes the cached order projection (it now carries the
        // recipient/decided-at for void attendees), so evict it here.
        cacheInvalidator.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);

        await auditLog.LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            request.Id,
            auditDescription,
            adminUserId,
            request.SenderUserId,
            nameof(User));

        await NotifyDecisionAsync(request, successful: true, reason: null, ct);
    }

    /// <summary>
    /// Performs the TicketTailor void(-to-hold)+reissue and mirrors the result into local
    /// attendee rows. Records the outcome on <paramref name="request"/>
    /// (<see cref="TicketTransferRequest.VendorResult"/> / VendorMessage / NewVendorTicketId)
    /// but does not persist the request itself — the caller decides. Three outcomes:
    /// <list type="bullet">
    /// <item>void fails → <c>Failed</c>; nothing mutated locally (manual fallback).</item>
    /// <item>void OK but issue fails → <c>VoidSucceededIssueFailed</c>; original attendee
    /// mirrored to Void locally and the hold id surfaced for a manual reissue.</item>
    /// <item>both OK → <c>Succeeded</c>; original Void + new Valid row written atomically,
    /// the new row keeping the ORIGINAL price so revenue/VAT stay anchored to the order.</item>
    /// </list>
    /// </summary>
    private async Task WriteToVendorAsync(TicketTransferRequest request, CancellationToken ct)
    {
        var attendee = request.OriginalTicketAttendee
            ?? await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing during vendor writeback.");

        // 1: void to hold — reserves THIS ticket's allocation (same ticket type) off-sale so
        // the reissue lands on the original class even if it is now closed/sold out. TT never
        // refunds or cancels the order on a void, so no cashflow moves.
        VoidIssuedTicketResult voidResult;
        try
        {
            voidResult = await vendor.VoidIssuedTicketAsync(attendee.VendorTicketId, voidToHold: true, ct);
        }
        catch (Exception ex)
        {
            // Any failure here (vendor error, HttpClient timeout/TaskCanceledException, transport)
            // means the void did not reliably commit — fall back to manual processing. If it did
            // commit, a retry voids again (404 -> NotFound) and the next sync reconciles the void.
            request.VendorResult = TicketTransferVendorResult.Failed;
            request.VendorMessage = $"Void failed ({VendorFailureDetail(ex)})";
            logger.LogWarning(ex,
                "TT void failed for transfer {TransferId} attendee {AttendeeId}; manual fallback required",
                request.Id, request.OriginalTicketAttendeeId);
            return;
        }

        // The void has committed and is irreversible. Detach the rest of the writeback from the
        // request-scoped token so an aborted/timed-out admin request can't cancel the reissue or
        // the local persistence — that would strand the void at TT with no Humans-side record.
        var commitCt = CancellationToken.None;

        // 2: issue replacement against the hold — same ticket type, like-for-like, no charge.
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
                ExternalReference: request.Id.ToString("N")), commitCt);
        }
        catch (Exception ex)
        {
            // The void has already committed at the vendor, so ANY issue-call failure — vendor
            // error, HttpClient timeout/TaskCanceledException, JSON parse — must be captured as
            // partial state and never propagated (that would lose the committed void). Mirror the
            // void locally so the Sender's holdings flip, and retain the hold id so the team can
            // finish the reissue by hand against that hold. Partial state — never silently dropped.
            request.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
            request.VendorHoldId = voidResult.HoldId; // lets an admin one-click retry from the held seat
            request.VendorMessage = voidResult.HoldId is null
                ? $"Issue failed ({VendorFailureDetail(ex)})"
                : $"Issue failed ({VendorFailureDetail(ex)}) — retry reissue from hold {voidResult.HoldId}";
            attendee.Status = TicketAttendeeStatus.Void;
            await ticketRepo.UpsertAttendeesAsync([attendee], commitCt);
            cacheInvalidator.InvalidateAfterTransfer(request.SenderUserId, receiverUserId: null);
            logger.LogError(ex,
                "TT issue failed for transfer {TransferId} after successful void; hold {HoldId} retained for manual reissue",
                request.Id, voidResult.HoldId);
            return;
        }

        // 3: write both attendee rows in one SaveChanges so there is no window where the seat
        // is Valid on both. The new row keeps the ORIGINAL price (TT's reissued listed_price
        // is informational and may drift; sync preserves our snapshot for API-issued tickets).
        var now = clock.GetCurrentInstant();
        attendee.Status = TicketAttendeeStatus.Void;
        await ticketRepo.UpsertAttendeesAsync(
            [BuildReissuedAttendee(attendee, request, issued.VendorTicketId, now), attendee], commitCt);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorHoldId = null; // consumed
        request.VendorMessage = voidResult.HoldId is null ? null : $"hold {voidResult.HoldId}";

        cacheInvalidator.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);
    }

    public async Task<TicketTransferRowDto> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("A reason is required to cancel a transfer.");

        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");
        EnsureNotMidProcessing(request);

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Rejected;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = reason;
        await transferRepo.UpdateAsync(request, ct);

        cacheInvalidator.InvalidateAfterTransfer(request.SenderUserId, request.ReceiverUserId);

        await auditLog.LogAsync(
            AuditAction.TicketTransferRejected,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer cancelled: {reason}",
            adminUserId,
            request.SenderUserId,
            nameof(User));

        await NotifyDecisionAsync(request, successful: false, reason: reason, ct);

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
        var attendee = await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct);
        var order = attendee?.TicketOrder;
        var siblingIds = order is not null
            ? (await ticketRepo.GetVendorTicketIdsForOrderAsync(order.Id, ct))
                .OrderBy(s => s, StringComparer.Ordinal).ToList()
            : (IReadOnlyList<string>)[];

        return new TicketTransferDetailDto(
            Row: row,
            OrderDashboardUrl: order?.VendorDashboardUrl,
            OriginalAttendeeVendorTicketId: attendee?.VendorTicketId ?? string.Empty,
            OriginalAttendeeEmail: attendee?.AttendeeEmail,
            OrderVendorId: order?.VendorOrderId ?? string.Empty,
            OrderPurchasedAt: order?.PurchasedAt ?? Instant.MinValue,
            OrderBuyerEmail: order?.BuyerEmail ?? string.Empty,
            SiblingVendorTicketIds: siblingIds);
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        transferRepo.CountPendingAsync(ct);

    // Notifications are best-effort: the request/decision is already persisted, so neither a failed
    // lookup nor a failed send may bubble up (P1 — would tell the user it failed and prompt a retry),
    // and each recipient is dispatched independently so one failure can't suppress the others
    // (P1 — e.g. a bad sender address must not stop the ticket-team alert).
    private async Task NotifyRequestedAsync(
        TicketTransferRequest request, TicketAttendee attendee, Guid senderUserId, CancellationToken ct)
    {
        var ticketLabel = TicketLabel(attendee.AttendeeName, attendee.VendorTicketId);
        var reviewUrl = $"/Tickets/Admin/Transfers/Detail/{request.Id}";
        var (senderEmail, senderName) = await SafeResolveSenderAsync(senderUserId, request.Id, ct);

        if (!string.IsNullOrWhiteSpace(senderEmail))
        {
            await SafeSendAsync(request.Id, "transfer-requested (sender)", () =>
                emailService.SendAsync(emailMessages.TicketTransferRequested(
                    senderEmail, senderName, request.ReceiverLegalName, ticketLabel, culture: null), ct));
        }

        await SafeSendAsync(request.Id, "transfer-requested (team)", () =>
            emailService.SendAsync(emailMessages.TicketTransferTeamNotification(
                senderName, request.ReceiverLegalName, request.ReceiverEmail,
                ticketLabel, request.SenderReason, reviewUrl), ct));
    }

    private async Task NotifyDecisionAsync(
        TicketTransferRequest request, bool successful, string? reason, CancellationToken ct)
    {
        var attendee = await SafeGetAttendeeAsync(request.OriginalTicketAttendeeId, request.Id, ct);
        var ticketLabel = TicketLabel(
            attendee?.AttendeeName ?? request.ReceiverLegalName,
            attendee?.VendorTicketId ?? string.Empty);
        var (senderEmail, senderName) = await SafeResolveSenderAsync(request.SenderUserId, request.Id, ct);

        if (!string.IsNullOrWhiteSpace(senderEmail))
        {
            await SafeSendAsync(request.Id, "transfer-decision (sender)", () =>
                emailService.SendAsync(emailMessages.TicketTransferDecision(
                    senderEmail, senderName, successful, ticketLabel,
                    request.ReceiverLegalName, reason, culture: null), ct));
        }

        if (!string.IsNullOrWhiteSpace(request.ReceiverEmail))
        {
            await SafeSendAsync(request.Id, "transfer-decision (receiver)", () =>
                emailService.SendAsync(emailMessages.TicketTransferDecision(
                    request.ReceiverEmail, request.ReceiverLegalName, successful, ticketLabel,
                    request.ReceiverLegalName, reason, culture: null), ct));
        }
    }

    private async Task SafeSendAsync(Guid transferId, string what, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {What} email for transfer {TransferId}", what, transferId);
        }
    }

    private async Task<(string? Email, string Name)> SafeResolveSenderAsync(
        Guid senderUserId, Guid transferId, CancellationToken ct)
    {
        try
        {
            return await ResolveSenderAsync(senderUserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve sender {SenderUserId} for transfer {TransferId} notifications",
                senderUserId, transferId);
            return (null, "there");
        }
    }

    private async Task<TicketAttendee?> SafeGetAttendeeAsync(Guid attendeeId, Guid transferId, CancellationToken ct)
    {
        try
        {
            return await ticketRepo.GetAttendeeByIdAsync(attendeeId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load attendee {AttendeeId} for transfer {TransferId} notifications",
                attendeeId, transferId);
            return null;
        }
    }

    private async Task<(string? Email, string Name)> ResolveSenderAsync(Guid senderUserId, CancellationToken ct)
    {
        var info = await userService.GetUserInfoAsync(senderUserId, ct);
        var email = await userEmailService.GetPrimaryEmailAsync(senderUserId, ct);
        var name = info?.BurnerName;
        return (email, string.IsNullOrWhiteSpace(name) ? "there" : name);
    }

    // VendorMessage is capped at 2000 chars and the vendor client embeds the raw TicketTailor
    // response body in the exception message, so bound the detail well under that — otherwise the
    // diagnostic UpdateAsync would throw and we'd lose the failure/partial record entirely.
    private const int MaxVendorDetailLength = 1500;

    // Kind-aware detail for a vendor write failure: the categorised Kind for a
    // TicketVendorWriteException, else the raw exception type (timeout/JSON/transport).
    private static string VendorFailureDetail(Exception ex)
    {
        var detail = ex is TicketVendorWriteException w
            ? $"{w.Kind}: {w.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";
        return detail.Length <= MaxVendorDetailLength ? detail : detail[..MaxVendorDetailLength] + "…";
    }

    // The new local attendee row for a reissued ticket: re-attached to the ORIGINAL order and
    // keeping the ORIGINAL price (like-for-like, no revenue drift). Shared by the initial
    // void+reissue and the retry path.
    private static TicketAttendee BuildReissuedAttendee(
        TicketAttendee original, TicketTransferRequest request, string newVendorTicketId, Instant now) =>
        new()
        {
            Id = Guid.NewGuid(),
            VendorTicketId = newVendorTicketId,
            TicketOrderId = original.TicketOrderId,
            AttendeeName = request.ReceiverLegalName,
            AttendeeEmail = request.ReceiverEmail,
            TicketTypeName = original.TicketTypeName,
            Price = original.Price,
            Status = TicketAttendeeStatus.Valid,
            VendorEventId = original.VendorEventId,
            SyncedAt = now,
            MatchedUserId = request.ReceiverUserId,
        };

    private static string TicketLabel(string attendeeName, string vendorTicketId) =>
        string.IsNullOrEmpty(vendorTicketId) ? attendeeName : $"{attendeeName} ({vendorTicketId})";

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
            OriginalAttendeeCheckedInAt: attendee.CheckedInAt,
            SenderUserId: r.SenderUserId,
            SenderDisplayName: sender?.BurnerName ?? "(unknown)",
            ReceiverUserId: r.ReceiverUserId,
            ReceiverLegalName: r.ReceiverLegalName,
            ReceiverEmail: r.ReceiverEmail,
            SenderReason: r.SenderReason,
            Status: r.Status,
            VendorResult: r.VendorResult,
            VendorMessage: r.VendorMessage,
            DecidedByUserId: r.DecidedByUserId,
            DecidedByDisplayName: decider?.BurnerName,
            AdminNotes: r.AdminNotes,
            RequestedAt: r.RequestedAt,
            DecidedAt: r.DecidedAt);
    }
}
