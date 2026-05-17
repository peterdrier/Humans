using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Ticket no Shifts" — humans with a Valid/CheckedIn matched
/// ticket attendee in the active vendor event who have NOT signed up for any
/// shift in the active EventSettings event (Pending and Confirmed signups
/// count as "has a shift"; Refused/Bailed/Cancelled/NoShow do not).
/// </summary>
public sealed class TicketNoShiftsAudience(
    ITicketQueryService tickets,
    IShiftSignupService shiftSignups,
    IShiftManagementService shiftManagement) : IMailerAudience
{
    public string Key => "ticket-no-shifts";
    public string DisplayName => "Ticket holders without a shift";
    public string MailerLiteGroupName => "Humans - Ticket no Shifts";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var activeEvent = await shiftManagement.GetActiveAsync();
        if (activeEvent is null) return new HashSet<Guid>();

        // Returns Valid/CheckedIn matched attendees (buyer-only excluded) — see ITicketQueryService.
        var ticketHolders = await tickets.GetUserIdsWithTicketsAsync();
        var shiftHavers = await shiftSignups.GetActiveCommittedUserIdsForEventAsync(activeEvent.Id, ct);

        var audience = new HashSet<Guid>(ticketHolders);
        audience.ExceptWith(shiftHavers);
        return audience;
    }
}
