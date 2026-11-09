using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed class TicketTransferVendorExchange(
    ITicketRepository ticketRepo,
    ITicketVendorService vendor,
    IClock clock,
    ILogger<TicketTransferService> logger) : IApplicationService
{
    /// <summary>
    /// Records the outcome on <paramref name="request"/> but does not persist it; the caller decides. A failed void
    /// changes nothing locally. A void that commits before a failed issue mirrors the original to Void and keeps the
    /// hold. Success writes the original Void and the new Valid row together, at the original price.
    /// </summary>
    internal async Task WriteToVendorAsync(TicketTransferRequest request, TicketAttendee attendee, CancellationToken ct)
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
            issued = await IssueAsync(request, voidResult.HoldId, commitCt);
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

    internal Task<VendorTicketDto> IssueAsync(
        TicketTransferRequest request, string? holdId, CancellationToken ct) =>
        vendor.IssueTicketAsync(new IssueTicketRequest(
            EventId: null,
            TicketTypeId: null,
            HoldId: holdId,
            FullName: request.ReceiverLegalName,
            Email: request.ReceiverEmail,
            SendEmail: true,
            ExternalReference: request.Id.ToString("N")), ct);

    internal async Task MirrorReissueAsync(
        TicketAttendee attendee, TicketTransferRequest request, string vendorTicketId, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        await ticketRepo.UpsertAttendeesAsync(
            [BuildReissuedAttendee(attendee, request, vendorTicketId, now)], ct);
    }

    // VendorMessage is capped at 2000 chars and the vendor client embeds the raw response body in the exception, so
    // bound the detail well under that, or the diagnostic write throws and the record is lost.
    private const int MaxVendorDetailLength = 1500;

    internal static string VendorFailureDetail(Exception ex)
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

}
