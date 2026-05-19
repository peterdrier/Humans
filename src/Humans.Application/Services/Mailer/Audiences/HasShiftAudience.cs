using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Has Shift" — humans with at least one Pending/Confirmed signup
/// in the active event, surfaced via the cached <see cref="IShiftView"/>
/// (<see cref="DTOs.Shifts.ShiftUserView.HasShift"/>).
/// </summary>
public sealed class HasShiftAudience(
    IShiftView shiftView,
    IUserService users) : IMailerAudience
{
    public string Key => "has-shift";
    public string DisplayName => "Volunteers with a shift signup";
    public string MailerLiteGroupName => "Humans - Has Shift";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await users.GetAllUserInfosAsync(ct);
        var ids = allUsers.Select(u => u.Id).ToList();
        var views = await shiftView.GetUsersAsync(ids, ct);
        return views
            .Where(kv => kv.Value.HasShift)
            .Select(kv => kv.Key)
            .ToHashSet();
    }
}
