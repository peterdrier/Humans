using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Meter: ticket sync error flag. Registered by the Tickets section (which owns the
/// <c>ticket_sync_states</c> table) per the push-model design in issue
/// nobodies-collective/Humans#581.
/// </summary>
public sealed class TicketSyncErrorMeterContributor : INotificationMeterContributor
{
    private readonly ITicketSyncService _ticketSyncService;

    public TicketSyncErrorMeterContributor(ITicketSyncService ticketSyncService)
    {
        _ticketSyncService = ticketSyncService;
    }

    public string Key => "TicketSyncError";

    public NotificationMeterScope Scope => NotificationMeterScope.Global;

    public bool IsVisibleTo(ClaimsPrincipal user) => user.IsInRole(RoleNames.Admin);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var inError = await _ticketSyncService.IsInErrorStateAsync(cancellationToken);
        if (!inError) return null;

        return new NotificationMeter
        {
            Title = "Ticket sync error",
            Count = 1,
            ActionUrl = "/Tickets",
            Priority = 4,
        };
    }
}
