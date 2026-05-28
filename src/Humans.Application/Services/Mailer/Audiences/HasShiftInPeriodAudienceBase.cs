using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// Base for the per-period "Has Shift" audiences. Membership is every human
/// with at least one Pending/Confirmed signup on a shift in <see cref="Period"/>
/// of the active event, surfaced via the cached <see cref="IShiftView"/>
/// (<see cref="DTOs.Shifts.ShiftUserView.HasShiftInPeriod"/>). Subclasses supply
/// the period plus the audience metadata.
/// </summary>
public abstract class HasShiftInPeriodAudienceBase(
    IShiftView shiftView,
    IUserServiceRead users) : MailerAudienceBase(users)
{
    /// <summary>The shift period this audience targets.</summary>
    protected abstract ShiftPeriod Period { get; }

    protected override async Task<IReadOnlySet<Guid>> ComputeRawMemberUserIdsAsync(CancellationToken ct)
    {
        var allUsers = await Users.GetAllUserInfosAsync(ct);
        var ids = allUsers.Select(u => u.Id).ToList();
        var views = await shiftView.GetUsersAsync(ids, ct);
        return views
            .Where(kv => kv.Value.HasShiftInPeriod(Period))
            .Select(kv => kv.Key)
            .ToHashSet();
    }
}
