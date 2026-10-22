using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Base.Threading;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Tickets.Contracts;
using NodaTime;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Services;

internal sealed class TicketTransferService(
    ITicketTransferRepository transferRepo,
    ITicketRepository ticketRepo,
    ITicketService holdings,
    ITicketVendorService vendor,
    IUserService userService,
    IUserEmailService userEmailService,
    IEmailService emailService,
    TicketsEmails emailMessages,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<TicketTransferService> logger) : ITicketTransferService
{

    // Fixed stripes coordinate instances: attendee id for creation, request id for decisions. Processing makes two
    // 90s vendor calls, so the five-minute acquisition timeout must outlast them.
    private static readonly TrackedLock[] DecisionLocks = Enumerable.Range(0, 32)
        .Select(i => new TrackedLock($"TicketTransfer.Decision[{i}]", timeout: TimeSpan.FromMinutes(5)))
        .ToArray();

    private static TrackedLock DecisionLockFor(Guid key) =>
        DecisionLocks[(uint)key.GetHashCode() % (uint)DecisionLocks.Length];

    public async Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(
        Guid userId, CancellationToken ct = default)
    {
        // The holdings read already filters to the current owner and stamps pending transfers.
        var held = await holdings.GetUserTicketHoldingsAsync(userId, ct);
        return held.Tickets
            .Where(t => t.Status != TicketAttendeeStatus.Void)
            .Select(t => new MyAttendeeRowDto(
                t,
                CanSendTransfer: t.Status == TicketAttendeeStatus.Valid
                    && t.CheckedInAt is null
                    && !t.HasPendingOutgoingTransfer))
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
        if (receiverInfo is null || receiverInfo.Id != receiverUserId || !receiverInfo.HasRequiredNameFields) return null;
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

    public async Task<TicketTransferMutationResult> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default)
    {
        if (dto.ReceiverUserId == senderUserId)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");

        using var creation = await DecisionLockFor(dto.OriginalAttendeeId).AcquireAsync(logger, ct);
        var attendee = await ticketRepo.GetAttendeeByIdAsync(dto.OriginalAttendeeId, ct);
        if (attendee is null)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");

        if (!TicketAttendeeOwnership.IsCurrentOwner(attendee, senderUserId))
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_NotCurrentHolder");

        if (attendee.Status != TicketAttendeeStatus.Valid)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_NotTransferable");

        // A gate scan keeps Status = Valid, so the check above doesn't catch a used ticket.
        if (attendee.CheckedInAt is not null)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_CheckedIn");

        var receiverInfo = await userService.GetUserInfoAsync(dto.ReceiverUserId, ct);
        if (receiverInfo is null || receiverInfo.Id != dto.ReceiverUserId)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");
        // Receiver must have a legal name; same message as not-found so the reason doesn't leak.
        if (!receiverInfo.HasRequiredNameFields)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");
        var receiverProfile = receiverInfo.Profile!;

        // Block duplicate pendings; ToDictionary elsewhere would crash on them.
        var existingPending = (await transferRepo.GetBySenderAsync(senderUserId, ct))
            .Any(r => r.OriginalTicketAttendeeId == dto.OriginalAttendeeId
                && r.Status == TicketTransferStatus.Pending);
        if (existingPending)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_AlreadyPending");

        var receiverLegalName = receiverProfile.FullName;
        var receiverEmail = await userEmailService.GetPrimaryEmailAsync(dto.ReceiverUserId, ct);
        if (receiverEmail is null)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");

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
        // The request is committed: a disconnected browser must not stop the notifications.
        ct = CancellationToken.None;

        await auditLog.LogAsync(
            AuditAction.TicketTransferRequested,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer requested: ticket {attendee.VendorTicketId} → {receiverLegalName}",
            senderUserId,
            dto.ReceiverUserId,
            nameof(User));

        await NotifyRequestedAsync(request, attendee, senderUserId, ct);

        return new TicketTransferMutationResult(await BuildRowDtoAsync(request, ct), null);
    }

    public async Task<TicketTransferMutationResult> CancelAsync(Guid transferRequestId, Guid senderUserId, CancellationToken ct = default)
    {
        using var decision = await DecisionLockFor(transferRequestId).AcquireAsync(logger, ct);
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct);
        if (request is null)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_NotFound");
        if (request.Status != TicketTransferStatus.Pending)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_OnlyPendingCanBeCancelled");
        if (request.SenderUserId != senderUserId)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_OnlySenderCanCancel");
        if (request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_CancelFailed");

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

        return new TicketTransferMutationResult(null, null);
    }

    public async Task<TicketTransferMutationResult> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        using var decision = await DecisionLockFor(transferRequestId).AcquireAsync(logger, ct);
        var (request, refusal) = await LoadPendingAsync(transferRequestId, ct);
        if (request is null) return RefuseAdmin(transferRequestId, adminUserId, refusal!);
        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            "Transfer marked successful (processed manually in TicketTailor)", ct);
        return new TicketTransferMutationResult(await BuildRowDtoAsync(request, CancellationToken.None), null);
    }

    public async Task<TicketTransferMutationResult> ProcessTransferAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        using var decision = await DecisionLockFor(transferRequestId).AcquireAsync(logger, ct);
        var (request, refusal) = await LoadPendingAsync(transferRequestId, ct);
        if (request is null) return RefuseAdmin(transferRequestId, adminUserId, refusal!);
        // A partial (already-voided) request must not be re-processed: it would void again and overwrite the partial
        // state.
        if (GetMidProcessingRefusal(request) is { } midProcessing)
            return RefuseAdmin(transferRequestId, adminUserId, midProcessing);

        // On vendor failure this records the outcome but does not throw.
        var attendee = request.OriginalTicketAttendee
            ?? await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct);
        if (attendee is null) return RefuseAdmin(transferRequestId, adminUserId, "Original attendee missing.");
        // Pending requests can outlive eligibility: recheck before the irreversible vendor void.
        if (attendee.CheckedInAt is not null || attendee.Status == TicketAttendeeStatus.CheckedIn)
            return RefuseAdmin(transferRequestId, adminUserId, "Original ticket has already been checked in and cannot be transferred.");
        if (attendee.Status != TicketAttendeeStatus.Valid)
            return RefuseAdmin(transferRequestId, adminUserId, "Original ticket is no longer valid and cannot be transferred.");
        await WriteToVendorAsync(request, attendee, ct);

        // A committed vendor void is irreversible: its outcome must persist even if the admin's request aborted.
        var persistCt = request.VendorResult is TicketTransferVendorResult.Succeeded
            or TicketTransferVendorResult.VoidSucceededIssueFailed
            ? CancellationToken.None
            : ct;

        if (request.VendorResult != TicketTransferVendorResult.Succeeded)
        {
            // Not transferred: persist and audit the diagnostic, leave the request Pending. No success emails.
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

            return RefuseAdmin(transferRequestId, adminUserId,
                request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed
                    ? $"Ticket was voided but the reissue failed ({request.VendorMessage}). Finish the reissue in TicketTailor, then use “Mark successful”."
                    : $"Automated void+reissue failed ({request.VendorMessage}). Process this transfer manually in TicketTailor, then use “Mark successful”.");
        }

        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            $"Transfer processed automatically (TT void+reissue OK, new ticket {request.NewVendorTicketId})", persistCt);
        return new TicketTransferMutationResult(await BuildRowDtoAsync(request, ct), null);
    }

    public async Task<TicketTransferMutationResult> RetryReissueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        // The original void is already committed; waiting must not cancel its reissue.
        using var decision = await DecisionLockFor(transferRequestId).AcquireAsync(logger, CancellationToken.None);
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct);
        if (request is null) return RefuseAdmin(transferRequestId, adminUserId, "Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending
            || request.VendorResult != TicketTransferVendorResult.VoidSucceededIssueFailed)
        {
            return RefuseAdmin(transferRequestId, adminUserId,
                "Retry is only available for a part-processed transfer (ticket voided, reissue pending).");
        }
        if (string.IsNullOrEmpty(request.VendorHoldId))
        {
            return RefuseAdmin(transferRequestId, adminUserId,
                "No hold id was recorded for this transfer — finish the reissue in TicketTailor and use “Mark successful”.");
        }

        var attendee = await ticketRepo.GetAttendeeByIdAsync(request.OriginalTicketAttendeeId, ct);
        if (attendee is null) return RefuseAdmin(transferRequestId, adminUserId, "Original attendee missing.");

        // Everything here follows a committed void, so an aborted admin request must not cancel the reissue and
        // strand the held seat.
        var commitCt = CancellationToken.None;

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
                ExternalReference: request.Id.ToString("N")), commitCt);
        }
        catch (Exception ex)
        {
            // Still failing: keep the partial state and the hold so the admin can retry. A non-request token so an
            // aborted retry keeps the updated diagnostic.
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
            return RefuseAdmin(transferRequestId, adminUserId,
                $"Reissue retry failed ({VendorFailureDetail(ex)}). Try again, or finish in TicketTailor and use “Mark successful”.");
        }

        // The reissue committed at the vendor, so the local write must complete even if the request aborted.
        var now = clock.GetCurrentInstant();
        await ticketRepo.UpsertAttendeesAsync(
            [BuildReissuedAttendee(attendee, request, issued.VendorTicketId, now)], CancellationToken.None);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = $"hold {request.VendorHoldId} (retried)";
        request.VendorHoldId = null;

        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            $"Reissue retried OK (new ticket {issued.VendorTicketId})", CancellationToken.None);
        return new TicketTransferMutationResult(await BuildRowDtoAsync(request, ct), null);
    }

    private async Task<(TicketTransferRequest? Request, string? Refusal)> LoadPendingAsync(Guid transferRequestId, CancellationToken ct)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct);
        if (request is null) return (null, "Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            return (null, "Only Pending transfers can be decided.");
        return (request, null);
    }

    private TicketTransferMutationResult RefuseAdmin(Guid transferRequestId, Guid adminUserId, string reason)
    {
        logger.LogWarning("Ticket transfer decision rejected for transfer {TransferId}, actor {ActorId}: {Reason}",
            transferRequestId, adminUserId, reason);
        return new TicketTransferMutationResult(null, null, reason);
    }

    // The ticket is already voided at the vendor, irreversibly. Cancel/Reject would drop it from the queue and strand
    // the receiver; the only ways forward are a retried or a manual reissue.
    private static string? GetMidProcessingRefusal(TicketTransferRequest request) =>
        request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed
            ? "This transfer is mid-processing — the ticket was already voided and is awaiting reissue. Finish it in TicketTailor, then use “Mark successful”."
            : null;

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
        // Finish notification follow-up once the decision has persisted.
        ct = CancellationToken.None;

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
    /// Records the outcome on <paramref name="request"/> but does not persist it; the caller decides. A failed void
    /// changes nothing locally. A void that commits before a failed issue mirrors the original to Void and keeps the
    /// hold. Success writes the original Void and the new Valid row together, at the original price.
    /// </summary>
    private async Task WriteToVendorAsync(TicketTransferRequest request, TicketAttendee attendee, CancellationToken ct)
    {

        // Void to hold: reserves this ticket's allocation off-sale so the reissue lands on the same ticket type even if
        // it is now sold out. The vendor never refunds or cancels the order on a void.
        VoidIssuedTicketResult voidResult;
        try
        {
            voidResult = await vendor.VoidIssuedTicketAsync(attendee.VendorTicketId, voidToHold: true, ct);
        }
        catch (Exception ex)
        {
            // Any failure means the void did not reliably commit. If it did, a retry voids again (404) and the next
            // sync reconciles.
            request.VendorResult = TicketTransferVendorResult.Failed;
            request.VendorMessage = $"Void failed ({VendorFailureDetail(ex)})";
            logger.LogWarning(ex,
                "TT void failed for transfer {TransferId} attendee {AttendeeId}; manual fallback required",
                request.Id, request.OriginalTicketAttendeeId);
            return;
        }

        // The void has committed: detach the rest from the request token, or an aborted request strands the void with
        // no local record.
        var commitCt = CancellationToken.None;

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
            // The void has already committed, so ANY issue failure must be recorded as partial state, never
            // propagated: that would lose the committed void. Mirror the void locally and keep the hold for a retry.
            request.VendorResult = TicketTransferVendorResult.VoidSucceededIssueFailed;
            request.VendorHoldId = voidResult.HoldId;
            request.VendorMessage = voidResult.HoldId is null
                ? $"Issue failed ({VendorFailureDetail(ex)})"
                : $"Issue failed ({VendorFailureDetail(ex)}) — retry reissue from hold {voidResult.HoldId}";
            attendee.Status = TicketAttendeeStatus.Void;
            await ticketRepo.UpsertAttendeesAsync([attendee], commitCt);
            logger.LogError(ex,
                "TT issue failed for transfer {TransferId} after successful void; hold {HoldId} retained for manual reissue",
                request.Id, voidResult.HoldId);
            return;
        }

        // One SaveChanges for both rows, so the seat is never Valid twice. The new row keeps the original price; the
        // vendor's reissued listed price may drift.
        var now = clock.GetCurrentInstant();
        attendee.Status = TicketAttendeeStatus.Void;
        await ticketRepo.UpsertAttendeesAsync(
            [BuildReissuedAttendee(attendee, request, issued.VendorTicketId, now), attendee], commitCt);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorHoldId = null;
        request.VendorMessage = voidResult.HoldId is null ? null : $"hold {voidResult.HoldId}";
    }

    public async Task<TicketTransferMutationResult> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return RefuseAdmin(transferRequestId, adminUserId, "A reason is required to cancel a transfer.");

        using var decision = await DecisionLockFor(transferRequestId).AcquireAsync(logger, ct);
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct);
        if (request is null) return RefuseAdmin(transferRequestId, adminUserId, "Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            return RefuseAdmin(transferRequestId, adminUserId, "Only Pending transfers can be decided.");
        if (GetMidProcessingRefusal(request) is { } midProcessing)
            return RefuseAdmin(transferRequestId, adminUserId, midProcessing);

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Rejected;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = reason;
        await transferRepo.UpdateAsync(request, ct);
        // Finish notification follow-up once the decision has persisted.
        ct = CancellationToken.None;

        await auditLog.LogAsync(
            AuditAction.TicketTransferRejected,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer cancelled: {reason}",
            adminUserId,
            request.SenderUserId,
            nameof(User));

        await NotifyDecisionAsync(request, successful: false, reason: reason, ct);

        return new TicketTransferMutationResult(await BuildRowDtoAsync(request, ct), null);
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

    // Best-effort: the decision is already persisted, so a failed lookup or send must not bubble up (it would prompt
    // a retry), and each recipient is sent independently so one failure can't suppress the others.
    private async Task NotifyRequestedAsync(
        TicketTransferRequest request, TicketAttendee attendee, Guid senderUserId, CancellationToken ct)
    {
        var ticketLabel = TicketLabel(attendee.AttendeeName, attendee.VendorTicketId);
        var reviewUrl = $"/Tickets/Admin/Transfers/Detail/{request.Id}";
        var (senderEmail, senderName, senderCulture) = await SafeResolveSenderAsync(senderUserId, request.Id, ct);

        if (!string.IsNullOrWhiteSpace(senderEmail))
        {
            await SafeSendAsync(request.Id, "transfer-requested (sender)", () =>
                emailService.SendAsync(emailMessages.TicketTransferRequested(
                    senderUserId, senderEmail, senderName, request.ReceiverLegalName, ticketLabel, senderCulture), ct), ct);
        }

        await SafeSendAsync(request.Id, "transfer-requested (team)", () =>
            emailService.SendAsync(emailMessages.TicketTransferTeamNotification(
                senderName, request.ReceiverLegalName, request.ReceiverEmail,
                ticketLabel, request.SenderReason, reviewUrl), ct), ct);
    }

    private async Task NotifyDecisionAsync(
        TicketTransferRequest request, bool successful, string? reason, CancellationToken ct)
    {
        var attendee = await SafeGetAttendeeAsync(request.OriginalTicketAttendeeId, request.Id, ct);
        var ticketLabel = TicketLabel(
            attendee?.AttendeeName ?? request.ReceiverLegalName,
            attendee?.VendorTicketId ?? string.Empty);
        var (senderEmail, senderName, senderCulture) = await SafeResolveSenderAsync(request.SenderUserId, request.Id, ct);

        if (!string.IsNullOrWhiteSpace(senderEmail))
        {
            await SafeSendAsync(request.Id, "transfer-decision (sender)", () =>
                emailService.SendAsync(emailMessages.TicketTransferDecision(
                    request.SenderUserId, senderEmail, senderName, successful, ticketLabel,
                    request.ReceiverLegalName, reason, senderCulture), ct), ct);
        }

        if (!string.IsNullOrWhiteSpace(request.ReceiverEmail))
        {
            var receiverCulture = await SafeResolveReceiverCultureAsync(request, ct);
            await SafeSendAsync(request.Id, "transfer-decision (receiver)", () =>
                emailService.SendAsync(emailMessages.TicketTransferDecision(
                    request.ReceiverUserId, request.ReceiverEmail, request.ReceiverLegalName, successful, ticketLabel,
                    request.ReceiverLegalName, reason, receiverCulture), ct), ct);
        }
    }

    private async Task SafeSendAsync(Guid transferId, string what, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await send();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {What} email for transfer {TransferId}", what, transferId);
        }
    }

    private async Task<(string? Email, string Name, string? Culture)> SafeResolveSenderAsync(
        Guid senderUserId, Guid transferId, CancellationToken ct)
    {
        try
        {
            return await ResolveSenderAsync(senderUserId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve sender {SenderUserId} for transfer {TransferId} notifications",
                senderUserId, transferId);
            return (null, "there", null);
        }
    }

    // A failed lookup falls back to English, never the acting admin's request culture.
    private async Task<string> SafeResolveReceiverCultureAsync(TicketTransferRequest request, CancellationToken ct)
    {
        try
        {
            var language = (await userService.GetUserInfoAsync(request.ReceiverUserId, ct))?.PreferredLanguage;
            return language.IsSupportedCultureCode() ? language! : CultureCatalog.DefaultCultureCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve receiver {ReceiverUserId} culture for transfer {TransferId} notification",
                request.ReceiverUserId, request.Id);
            return "en";
        }
    }

    private async Task<TicketAttendee?> SafeGetAttendeeAsync(Guid attendeeId, Guid transferId, CancellationToken ct)
    {
        try
        {
            return await ticketRepo.GetAttendeeByIdAsync(attendeeId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load attendee {AttendeeId} for transfer {TransferId} notifications",
                attendeeId, transferId);
            return null;
        }
    }

    private async Task<(string? Email, string Name, string? Culture)> ResolveSenderAsync(Guid senderUserId, CancellationToken ct)
    {
        var info = await userService.GetUserInfoAsync(senderUserId, ct);
        var email = await userEmailService.GetPrimaryEmailAsync(senderUserId, ct);
        var name = info?.BurnerName;
        var language = info?.PreferredLanguage;
        return (email, string.IsNullOrWhiteSpace(name) ? "there" : name,
            language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode);
    }

    // VendorMessage is capped at 2000 chars and the vendor client embeds the raw response body in the exception, so
    // bound the detail well under that, or the diagnostic write throws and the record is lost.
    private const int MaxVendorDetailLength = 1500;

    private static string VendorFailureDetail(Exception ex)
    {
        var detail = ex is TicketVendorWriteException w
            ? $"{w.Kind}: {w.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";
        if (detail.Length <= MaxVendorDetailLength) return detail;
        var length = MaxVendorDetailLength;
        if (char.IsHighSurrogate(detail[length - 1]) && char.IsLowSurrogate(detail[length]))
            length--;
        return detail[..length] + "…";
    }

    // The original order and the original price: like-for-like, no revenue drift.
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

    private async Task<TicketTransferRowDto> BuildRowDtoAsync(TicketTransferRequest r, CancellationToken ct) =>
        BuildRowDto(r, await ResolveAttendeeAsync(r, ct));

    private async Task<IReadOnlyList<TicketTransferRowDto>> BuildRowDtosAsync(
        IReadOnlyList<TicketTransferRequest> rows, CancellationToken ct)
    {
        var result = new List<TicketTransferRowDto>(rows.Count);
        foreach (var r in rows)
            result.Add(BuildRowDto(r, await ResolveAttendeeAsync(r, ct)));
        return result;
    }

    private async Task<TicketAttendee> ResolveAttendeeAsync(
        TicketTransferRequest r, CancellationToken ct) =>
        r.OriginalTicketAttendee
            ?? await ticketRepo.GetAttendeeByIdAsync(r.OriginalTicketAttendeeId, ct)
            ?? throw new InvalidOperationException("Original attendee missing.");

    private static TicketTransferRowDto BuildRowDto(
        TicketTransferRequest r,
        TicketAttendee attendee)
    {
        return new TicketTransferRowDto(
            Id: r.Id,
            OriginalAttendeeId: r.OriginalTicketAttendeeId,
            OriginalAttendeeName: attendee.AttendeeName,
            TicketTypeName: attendee.TicketTypeName,
            OriginalAttendeeStatus: attendee.Status,
            OriginalAttendeeCheckedInAt: attendee.CheckedInAt,
            SenderUserId: r.SenderUserId,
            ReceiverUserId: r.ReceiverUserId,
            ReceiverLegalName: r.ReceiverLegalName,
            ReceiverEmail: r.ReceiverEmail,
            SenderReason: r.SenderReason,
            Status: r.Status,
            VendorResult: r.VendorResult,
            VendorMessage: r.VendorMessage,
            DecidedByUserId: r.DecidedByUserId,
            AdminNotes: r.AdminNotes,
            RequestedAt: r.RequestedAt,
            DecidedAt: r.DecidedAt);
    }
}
