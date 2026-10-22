using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// Pending and Confirmed signups count as a shift; Refused, Bailed, Cancelled and NoShow don't
/// (<see cref="ShiftUserSummary.HasShift"/>).
/// </summary>
internal sealed class TicketNoShiftsAudience(
    ITicketService tickets,
    IShiftView shiftView,
    IUserService users,
    ICommunicationPreferenceService prefs) : MailerLiteAudienceBase(users, prefs)
{
    public override string Key => "ticket-no-shifts";
    public override string DisplayName => "Ticket holders without a shift";
    public override string MailerLiteGroupName => "Humans - Ticket no Shifts";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var ticketHolders = await CurrentEventTicketHolders.ForCurrentEventAsync(tickets, ct);
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
