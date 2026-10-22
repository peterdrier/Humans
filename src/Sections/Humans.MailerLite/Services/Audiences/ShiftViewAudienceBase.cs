using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>Reads the cached <see cref="IShiftView"/>, so the debug screen renders without DB queries.</summary>
internal abstract class ShiftViewAudienceBase(
    IShiftView shiftView,
    IUserService users,
    ICommunicationPreferenceService prefs) : MailerLiteAudienceBase(users, prefs)
{
    protected abstract bool Matches(ShiftUserSummary summary);

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        var ids = allUsers.Select(u => u.Id).ToList();
        var views = await shiftView.GetUsersAsync(ids, ct);
        return views
            .Where(kv => Matches(kv.Value))
            .Select(kv => kv.Key)
            .ToHashSet();
    }
}
