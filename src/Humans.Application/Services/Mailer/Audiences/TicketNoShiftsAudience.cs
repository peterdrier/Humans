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
/// <remarks>
/// Reads shifts state through <see cref="IShiftView"/> — the cached read
/// surface — so opening the Mailer audience debug page doesn't burn DB
/// queries on every render. <see cref="Dtos.Shifts.ShiftUserView.HasShift"/>
/// already encodes the Pending/Confirmed-on-active-event rule.
/// </remarks>
public sealed class TicketNoShiftsAudience(
    ITicketQueryService tickets,
    IShiftView shiftView) : IMailerAudience
{
    public string Key => "ticket-no-shifts";
    public string DisplayName => "Ticket holders without a shift";
    public string MailerLiteGroupName => "Humans - Ticket no Shifts";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        // Returns Valid/CheckedIn matched attendees (buyer-only excluded) — see ITicketQueryService.
        var ticketHolders = await tickets.GetUserIdsWithTicketsAsync();
        if (ticketHolders.Count == 0) return new HashSet<Guid>();

        var views = await shiftView.GetUsersAsync(ticketHolders, ct);
        var audience = new HashSet<Guid>();
        foreach (var uid in ticketHolders)
        {
            if (!views.TryGetValue(uid, out var view) || !view.HasShift)
                audience.Add(uid);
        }
        return audience;
    }
}
