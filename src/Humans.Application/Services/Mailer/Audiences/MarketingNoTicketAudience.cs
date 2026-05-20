using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Marketing no Ticket" — humans who have explicitly opted in to the
/// Marketing communication category (<see cref="UserInfo.MarketingOptedOut"/> == false)
/// AND do not currently hold a ticket in the active vendor event.
/// Users with no Marketing preference row (default-off) or who hold a ticket are excluded.
/// </summary>
public sealed class MarketingNoTicketAudience(
    IUserService users,
    ITicketQueryService tickets) : IMailerAudience
{
    public string Key => "marketing-no-ticket";
    public string DisplayName => "Marketing opt-ins without a ticket";
    public string MailerLiteGroupName => "Humans - Marketing no Ticket";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var ticketHolders = await tickets.GetUserIdsWithTicketsAsync();
        var allUsers = await users.GetAllUserInfosAsync(ct);
        return allUsers
            .Where(u => u.MarketingOptedOut == false && !ticketHolders.Contains(u.Id))
            .Select(u => u.Id)
            .ToHashSet();
    }
}
