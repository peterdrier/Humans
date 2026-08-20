using Humans.Shifts.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services.Audiences;

/// <summary>
/// "Humans - Has Shift" — humans with at least one Pending/Confirmed signup
/// in the active event, surfaced via the cached <see cref="IShiftView"/>
/// (<see cref="DTOs.Shifts.ShiftUserView.HasShift"/>).
/// </summary>
internal sealed class HasShiftAudience(
    IShiftView shiftView,
    IUserServiceRead users) : MailerLiteAudienceBase(users)
{
    public override string Key => "has-shift";
    public override string DisplayName => "Volunteers with a shift signup";
    public override string MailerLiteGroupName => "Humans - Has Shift";

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        var ids = allUsers.Select(u => u.Id).ToList();
        var views = await shiftView.GetUsersAsync(ids, ct);
        return views
            .Where(kv => kv.Value.HasShift)
            .Select(kv => kv.Key)
            .ToHashSet();
    }
}
