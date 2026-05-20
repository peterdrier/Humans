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
    ITicketQueryService tickets) : MailerAudienceBase(users)
{
    public override string Key => "marketing-no-ticket";
    public override string DisplayName => "Marketing opt-ins without a ticket";
    public override string MailerLiteGroupName => "Humans - Marketing no Ticket";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var ticketHolders = await tickets.GetUserIdsWithTicketsAsync();
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        return allUsers
            .Where(u => u.MarketingOptedOut == false && !ticketHolders.Contains(u.Id))
            .Select(u => u.Id)
            .ToHashSet();
    }
}
