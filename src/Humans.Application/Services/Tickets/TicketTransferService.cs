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
/// request; the ticket team processes the void+reissue manually in TicketTailor
/// and then marks the request successful ("Approved") or cancels it with a reason
/// ("Rejected"). This service never calls the vendor — the next ticket sync
/// reconciles local attendee rows. Requests email the Sender + the ticket team;
/// decisions email the Sender + Receiver.
/// </summary>
public sealed class TicketTransferService(
    ITicketTransferRepository transferRepo,
    ITicketRepository ticketRepo,
    IUserServiceRead userService,
    IUserEmailService userEmailService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLog,
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
                    CanSendTransfer: a.Status == TicketAttendeeStatus.Valid && owner && !pending,
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

    public async Task<TicketTransferRowDto> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default)
    {
        var request = await transferRepo.GetByIdAsync(transferRequestId, ct)
            ?? throw new InvalidOperationException("Transfer not found.");
        if (request.Status != TicketTransferStatus.Pending)
            throw new InvalidOperationException("Only Pending transfers can be decided.");

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Approved;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = adminNotes;
        await transferRepo.UpdateAsync(request, ct);

        await auditLog.LogAsync(
            AuditAction.TicketTransferApproved,
            nameof(TicketTransferRequest),
            request.Id,
            "Transfer marked successful (processed manually in TicketTailor)",
            adminUserId,
            request.SenderUserId,
            nameof(User));

        await NotifyDecisionAsync(request, successful: true, reason: null, ct);

        return await BuildRowDtoAsync(request, ct);
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

        var now = clock.GetCurrentInstant();
        request.Status = TicketTransferStatus.Rejected;
        request.DecidedByUserId = adminUserId;
        request.DecidedAt = now;
        request.AdminNotes = reason;
        await transferRepo.UpdateAsync(request, ct);

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
            SenderUserId: r.SenderUserId,
            SenderDisplayName: sender?.BurnerName ?? "(unknown)",
            ReceiverUserId: r.ReceiverUserId,
            ReceiverLegalName: r.ReceiverLegalName,
            ReceiverEmail: r.ReceiverEmail,
            SenderReason: r.SenderReason,
            Status: r.Status,
            DecidedByUserId: r.DecidedByUserId,
            DecidedByDisplayName: decider?.BurnerName,
            AdminNotes: r.AdminNotes,
            RequestedAt: r.RequestedAt,
            DecidedAt: r.DecidedAt);
    }
}
