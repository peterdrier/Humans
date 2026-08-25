using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// Base for every audience defined by a person's shift signups. Membership is each human
/// whose cached <see cref="ShiftUserSummary"/> satisfies <see cref="Matches"/>; subclasses
/// supply that predicate plus the audience metadata. Reading through the cached
/// <see cref="IShiftView"/> is what lets the debug screen render without DB queries.
/// </summary>
internal abstract class ShiftViewAudienceBase(
    IShiftView shiftView,
    IUserServiceRead users) : MailerLiteAudienceBase(users)
{
    /// <summary>Whether this person's shift signups put them in the audience.</summary>
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
