using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed class TicketTransferReader(
    ITicketTransferRepository transferRepo,
    ITicketRepository ticketRepo,
    ITicketService holdings,
    IUserService userService) : IApplicationService
{
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
        if (receiverInfo is null || receiverInfo.IsTombstone
            || receiverInfo.Id != receiverUserId || !receiverInfo.HasRequiredNameFields) return null;
        var receiverEmail = receiverInfo.Email;
        if (string.IsNullOrWhiteSpace(receiverEmail)) return null;

        return new TicketTransferConfirmDto(
            AttendeeId: attendee.Id,
            AttendeeName: attendee.AttendeeName,
            VendorTicketId: attendee.VendorTicketId,
            ReceiverUserId: receiverUserId,
            ReceiverLegalName: receiverInfo.Profile!.FullName,
            ReceiverEmail: receiverEmail);
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

        var attendee = await ResolveAttendeeAsync(request, ct);
        var row = BuildRowDto(request, attendee);
        var order = attendee.TicketOrder;
        var siblingIds = order is not null
            ? (await ticketRepo.GetVendorTicketIdsForOrderAsync(order.Id, ct))
                .OrderBy(s => s, StringComparer.Ordinal).ToList()
            : (IReadOnlyList<string>)[];

        return new TicketTransferDetailDto(
            Row: row,
            OrderDashboardUrl: order?.VendorDashboardUrl,
            OriginalAttendeeVendorTicketId: attendee.VendorTicketId,
            OriginalAttendeeEmail: attendee.AttendeeEmail,
            OrderVendorId: order?.VendorOrderId ?? string.Empty,
            OrderPurchasedAt: order?.PurchasedAt ?? Instant.MinValue,
            OrderBuyerEmail: order?.BuyerEmail ?? string.Empty,
            SiblingVendorTicketIds: siblingIds);
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        transferRepo.CountPendingAsync(ct);

    internal async Task<TicketTransferRowDto> BuildRowDtoAsync(TicketTransferRequest r, CancellationToken ct) =>
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
