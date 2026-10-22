using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>Explicit opt-ins only (no Marketing row is excluded), minus current ticket holders.</summary>
internal sealed class MarketingNoTicketAudience(
    IUserService users,
    ITicketService tickets,
    ICommunicationPreferenceService prefs) : MailerLiteAudienceBase(users, prefs)
{
    public override string Key => "marketing-no-ticket";
    public override string DisplayName => "Marketing opt-ins without a ticket";
    public override string MailerLiteGroupName => "Humans - Marketing no Ticket";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var ticketHolders = await CurrentEventTicketHolders.ForCurrentEventAsync(tickets, ct);
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        var marketing = await MarketingPreferencesAsync(ct);
        return allUsers
            .Where(u => marketing.GetValueOrDefault(u.Id) is { OptedOut: false } && !ticketHolders.Contains(u.Id))
            .Select(u => u.Id)
            .ToHashSet();
    }
}
