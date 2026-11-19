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
    IEmailService emailService,
    TicketsEmails emailMessages,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<TicketTransferService> logger) : ITicketTransferService
{
    private readonly TicketTransferNotices notices = new(ticketRepo, userService, emailService, emailMessages, logger);
    private readonly TicketTransferVendorExchange exchange = new(ticketRepo, vendor, clock, logger);
    private readonly TicketTransferReader reader = new(transferRepo, ticketRepo, holdings, userService);

    private const int MaxTransferTextLength = 1000;
    private const string AdminNotesTooLong = "Notes cannot exceed 1,000 characters.";
    private const string ReceiverEmailMissing = "Receiver email is missing. Finish the transfer manually in TicketTailor and use “Mark successful”.";

    // Fixed stripes coordinate instances: attendee id for creation, request id for decisions. Processing makes two
    // 90s vendor calls, so the five-minute acquisition timeout must outlast them.
    private static readonly TrackedLock[] DecisionLocks = Enumerable.Range(0, 32)
        .Select(i => new TrackedLock($"TicketTransfer.Decision[{i}]", timeout: TimeSpan.FromMinutes(5)))
        .ToArray();

    private static TrackedLock DecisionLockFor(Guid key) =>
        DecisionLocks[(uint)key.GetHashCode() % (uint)DecisionLocks.Length];

    public Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetMyAttendeesAsync(userId, ct);

    public Task<TicketTransferConfirmDto?> GetConfirmationAsync(
        Guid attendeeId, Guid receiverUserId, Guid senderUserId, CancellationToken ct = default) =>
        reader.GetConfirmationAsync(attendeeId, receiverUserId, senderUserId, ct);

    public async Task<TicketTransferMutationResult> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default)
    {
        if (dto.Reason.Length > MaxTransferTextLength)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_ReasonTooLong");

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
        if (receiverInfo is null || receiverInfo.IsTombstone || receiverInfo.Id != dto.ReceiverUserId)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");
        // Receiver must have a legal name; same message as not-found so the reason doesn't leak.
        if (!receiverInfo.HasRequiredNameFields)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_InvalidSelection");

        // Block duplicate pendings; ToDictionary elsewhere would crash on them.
        var existingPending = (await transferRepo.GetBySenderAsync(senderUserId, ct))
            .Any(r => r.OriginalTicketAttendeeId == dto.OriginalAttendeeId
                && r.Status == TicketTransferStatus.Pending);
        if (existingPending)
            return TicketTransferMutationResult.Refused("Tickets_TicketTransfer_AlreadyPending");

        var receiverLegalName = receiverInfo.FullName;
        var receiverEmail = receiverInfo.Email;
        if (string.IsNullOrWhiteSpace(receiverEmail))
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
            TicketsAuditActions.TicketTransferRequested,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer requested: ticket {attendee.VendorTicketId} → {receiverLegalName}",
            senderUserId,
            dto.ReceiverUserId,
            AuditSubjectTypes.User);

        await notices.NotifyRequestedAsync(request, attendee, senderUserId, ct);

        return new TicketTransferMutationResult(await reader.BuildRowDtoAsync(request, ct), null);
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
            TicketsAuditActions.TicketTransferCancelled,
            nameof(TicketTransferRequest),
            request.Id,
            "Transfer cancelled by Sender",
            senderUserId);

        return new TicketTransferMutationResult(null, null);
    }

    public async Task<TicketTransferMutationResult> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        if (adminNotes is { Length: > MaxTransferTextLength })
            return RefuseAdmin(transferRequestId, adminUserId, AdminNotesTooLong);

        using var decision = await DecisionLockFor(transferRequestId).AcquireAsync(logger, ct);
        var (request, refusal) = await LoadPendingAsync(transferRequestId, ct);
        if (request is null) return RefuseAdmin(transferRequestId, adminUserId, refusal!);
        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            "Transfer marked successful (processed manually in TicketTailor)", ct);
        return new TicketTransferMutationResult(await reader.BuildRowDtoAsync(request, CancellationToken.None), null);
    }

    public async Task<TicketTransferMutationResult> ProcessTransferAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        if (adminNotes is { Length: > MaxTransferTextLength })
            return RefuseAdmin(transferRequestId, adminUserId, AdminNotesTooLong);

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
        if (string.IsNullOrWhiteSpace(request.ReceiverEmail))
            return RefuseAdmin(transferRequestId, adminUserId, ReceiverEmailMissing);
        await exchange.WriteToVendorAsync(request, attendee, ct);

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
                TicketsAuditActions.TicketTransferAutoFailed,
                nameof(TicketTransferRequest),
                request.Id,
                request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed
                    ? $"Automated process PARTIAL — {request.VendorMessage}; finish reissue in TicketTailor, then Mark successful"
                    : $"Automated process FAILED — {request.VendorMessage}; process manually in TicketTailor",
                adminUserId,
                request.SenderUserId,
                AuditSubjectTypes.User);

            return RefuseAdmin(transferRequestId, adminUserId,
                request.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed
                    ? $"Ticket was voided but the reissue failed ({request.VendorMessage}). Finish the reissue in TicketTailor, then use “Mark successful”."
                    : $"Automated void+reissue failed ({request.VendorMessage}). Process this transfer manually in TicketTailor, then use “Mark successful”.");
        }

        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            $"Transfer processed automatically (TT void+reissue OK, new ticket {request.NewVendorTicketId})", persistCt);
        return new TicketTransferMutationResult(await reader.BuildRowDtoAsync(request, ct), null);
    }

    public async Task<TicketTransferMutationResult> RetryReissueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        if (adminNotes is { Length: > MaxTransferTextLength })
            return RefuseAdmin(transferRequestId, adminUserId, AdminNotesTooLong);

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

        if (string.IsNullOrWhiteSpace(request.ReceiverEmail))
            return RefuseAdmin(transferRequestId, adminUserId, ReceiverEmailMissing);

        // Everything here follows a committed void, so an aborted admin request must not cancel the reissue and
        // strand the held seat.
        var commitCt = CancellationToken.None;

        VendorTicketDto issued;
        try
        {
            issued = await exchange.IssueAsync(request, request.VendorHoldId, commitCt);
        }
        catch (Exception ex)
        {
            // Still failing: keep the partial state and the hold so the admin can retry. A non-request token so an
            // aborted retry keeps the updated diagnostic.
            request.VendorMessage =
                $"Reissue retry failed ({TicketTransferVendorExchange.VendorFailureDetail(ex)}) — retry again or finish in TicketTailor against hold {request.VendorHoldId}";
            await transferRepo.UpdateAsync(request, CancellationToken.None);
            await auditLog.LogAsync(
                TicketsAuditActions.TicketTransferAutoFailed,
                nameof(TicketTransferRequest),
                request.Id,
                $"Reissue retry failed — {TicketTransferVendorExchange.VendorFailureDetail(ex)}",
                adminUserId,
                request.SenderUserId,
                AuditSubjectTypes.User);
            logger.LogError(ex, "Reissue retry failed for transfer {TransferId} against hold {HoldId}",
                request.Id, request.VendorHoldId);
            return RefuseAdmin(transferRequestId, adminUserId,
                $"Reissue retry failed ({TicketTransferVendorExchange.VendorFailureDetail(ex)}). Try again, or finish in TicketTailor and use “Mark successful”.");
        }

        // The reissue committed at the vendor, so the local write must complete even if the request aborted.
        await exchange.MirrorReissueAsync(attendee, request, issued.VendorTicketId, CancellationToken.None);

        request.VendorResult = TicketTransferVendorResult.Succeeded;
        request.NewVendorTicketId = issued.VendorTicketId;
        request.VendorMessage = $"hold {request.VendorHoldId} (retried)";
        request.VendorHoldId = null;

        await MarkApprovedAsync(
            request, adminUserId, adminNotes,
            $"Reissue retried OK (new ticket {issued.VendorTicketId})", CancellationToken.None);
        return new TicketTransferMutationResult(await reader.BuildRowDtoAsync(request, ct), null);
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
            TicketsAuditActions.TicketTransferApproved,
            nameof(TicketTransferRequest),
            request.Id,
            auditDescription,
            adminUserId,
            request.SenderUserId,
            AuditSubjectTypes.User);

        await notices.NotifyDecisionAsync(request, successful: true, reason: null, ct);
    }

    public async Task<TicketTransferMutationResult> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string reason, CancellationToken ct = default)
    {
        if (reason is { Length: > MaxTransferTextLength })
            return RefuseAdmin(transferRequestId, adminUserId, AdminNotesTooLong);

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
            TicketsAuditActions.TicketTransferRejected,
            nameof(TicketTransferRequest),
            request.Id,
            $"Transfer cancelled: {reason}",
            adminUserId,
            request.SenderUserId,
            AuditSubjectTypes.User);

        await notices.NotifyDecisionAsync(request, successful: false, reason: reason, ct);

        return new TicketTransferMutationResult(await reader.BuildRowDtoAsync(request, ct), null);
    }

    public Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default) =>
        reader.GetByStatusAsync(status, ct);

    public Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(Guid userId, CancellationToken ct = default) =>
        reader.GetBySenderAsync(userId, ct);

    public Task<TicketTransferDetailDto?> GetDetailAsync(Guid transferRequestId, CancellationToken ct = default) =>
        reader.GetDetailAsync(transferRequestId, ct);

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        reader.CountPendingAsync(ct);

}
