using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Ticket" — humans with a Valid/CheckedIn matched ticket
/// attendee in the active vendor event (buyer-only excluded — see
/// <see cref="ITicketQueryService.GetUserIdsWithTicketsAsync"/>).
/// </summary>
public sealed class HasTicketAudience(
    ITicketQueryService tickets,
    IUserService users) : MailerAudienceBase(users)
{
    public override string Key => "has-ticket";
    public override string DisplayName => "Ticket holders";
    public override string MailerLiteGroupName => "Humans - Has Ticket";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        _ = ct;
        return await tickets.GetUserIdsWithTicketsAsync();
    }
}
