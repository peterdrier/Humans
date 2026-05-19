using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Tickets;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Ticket" — humans with a Valid/CheckedIn matched ticket
/// attendee in the active vendor event (buyer-only excluded — see
/// <see cref="ITicketQueryService.GetUserIdsWithTicketsAsync"/>).
/// </summary>
public sealed class HasTicketAudience(
    ITicketQueryService tickets) : IMailerAudience
{
    public string Key => "has-ticket";
    public string DisplayName => "Ticket holders";
    public string MailerLiteGroupName => "Humans - Has Ticket";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        _ = ct;
        return await tickets.GetUserIdsWithTicketsAsync();
    }
}
