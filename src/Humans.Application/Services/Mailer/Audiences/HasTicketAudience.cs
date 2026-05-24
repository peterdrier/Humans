using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Ticket" — humans with a Valid/CheckedIn matched ticket
/// attendee in the active vendor event (buyer-only excluded — see
/// derived from the ticket order projection).
/// </summary>
public sealed class HasTicketAudience(
    ITicketServiceRead tickets,
    IUserServiceRead users) : MailerAudienceBase(users)
{
    public override string Key => "has-ticket";
    public override string DisplayName => "Ticket holders";
    public override string MailerLiteGroupName => "Humans - Has Ticket";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var orders = await tickets.GetTicketOrdersAsync(ct);
        return orders
            .Where(o => o.IsCurrentEvent)
            .SelectMany(o => o.Attendees)
            .Where(a => a.MatchedUserId.HasValue
                && a.Status is TicketAttendeeStatus.Valid or TicketAttendeeStatus.CheckedIn)
            .Select(a => a.MatchedUserId!.Value)
            .ToHashSet();
    }
}
