using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

internal sealed class HasTicketAudience(
    ITicketService tickets,
    IUserService users,
    ICommunicationPreferenceService prefs) : MailerLiteAudienceBase(users, prefs)
{
    public override string Key => "has-ticket";
    public override string DisplayName => "Ticket holders";
    public override string MailerLiteGroupName => "Humans - Has Ticket";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        return await CurrentEventTicketHolders.ForCurrentEventAsync(tickets, ct);
    }
}
