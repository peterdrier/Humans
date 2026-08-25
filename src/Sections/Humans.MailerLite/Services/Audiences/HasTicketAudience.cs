using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// "Humans - Has Ticket" — humans holding a ticket to the active vendor event, as
/// defined by <see cref="CurrentEventTicketHolders.ForCurrentEventAsync"/>.
/// </summary>
internal sealed class HasTicketAudience(
    ITicketServiceRead tickets,
    IUserServiceRead users) : MailerLiteAudienceBase(users)
{
    public override string Key => "has-ticket";
    public override string DisplayName => "Ticket holders";
    public override string MailerLiteGroupName => "Humans - Has Ticket";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        return await CurrentEventTicketHolders.ForCurrentEventAsync(tickets, ct);
    }
}
