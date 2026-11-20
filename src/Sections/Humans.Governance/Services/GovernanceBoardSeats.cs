using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Board seats are added and ended here; Auth stores them as the Board role, which the rest of the app checks.
/// Auth audits each change. The caller authorizes against <c>PolicyNames.RoleAssignmentManage</c>.
/// </summary>
internal sealed class GovernanceBoardSeats(
    IRoleAssignmentService roles,
    IRoleAssignmentWrite roleWrite,
    IUserService users,
    IClock clock) : IApplicationService
{
    public const string InactiveAccount = nameof(InactiveAccount);

    /// <summary>Only an active account can take a seat; anything else fails with <see cref="InactiveAccount"/>.</summary>
    public async Task<RoleAssignmentResult> AddAsync(Guid userId, Guid actorUserId, CancellationToken ct = default) =>
        await users.GetUserInfoAsync(userId, ct) is { IsActive: true } member
            ? await roleWrite.AssignRoleAsync(member.Id, RoleNames.Board, actorUserId, notes: null, ct)
            : new RoleAssignmentResult(false, InactiveAccount);

    public async Task<RoleAssignmentResult> EndAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var seat = (await roles.GetByUserIdAsync(userId, ct))
            .FirstOrDefault(a => string.Equals(a.RoleName, RoleNames.Board, StringComparison.Ordinal) && a.IsActive(now));
        return seat is null
            ? new RoleAssignmentResult(false, "RoleNotActive")
            : await roleWrite.EndRoleAsync(seat.Id, actorUserId, notes: null, ct);
    }
}
