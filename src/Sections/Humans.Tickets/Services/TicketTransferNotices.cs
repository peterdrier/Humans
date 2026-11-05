using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Users.Contracts;

namespace Humans.Tickets.Services;

internal sealed class TicketTransferNotices(
    ITicketRepository ticketRepo,
    IUserService userService,
    IEmailService emailService,
    TicketsEmails emailMessages,
    ILogger logger) : IApplicationService
{
    // Best-effort: the decision is already persisted, so a failed lookup or send must not bubble up (it would prompt
    // a retry), and each recipient is sent independently so one failure can't suppress the others.
    internal async Task NotifyRequestedAsync(
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

    internal async Task NotifyDecisionAsync(
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
        var email = info?.Email;
        var name = info?.BurnerName;
        var language = info?.PreferredLanguage;
        return (email, string.IsNullOrWhiteSpace(name) ? "there" : name,
            language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode);
    }

    private static string TicketLabel(string attendeeName, string vendorTicketId) =>
        string.IsNullOrEmpty(vendorTicketId) ? attendeeName : $"{attendeeName} ({vendorTicketId})";

}
