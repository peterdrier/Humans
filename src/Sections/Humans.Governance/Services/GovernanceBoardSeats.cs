using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Board seats are added and ended here; Auth stores them as the Board role, which the rest of the app checks.
/// Auth audits each seat change. The Board offices (President, …) are stored here and audited here.
/// The caller authorizes against <c>PolicyNames.RoleAssignmentManage</c>.
/// </summary>
internal sealed class GovernanceBoardSeats(
    IRoleAssignmentService roles,
    IRoleAssignmentWrite roleWrite,
    IUserService users,
    IGovernanceBodyRepository repository,
    IAuditLogService audit,
    GovernanceBodySync bodySync,
    IClock clock) : IApplicationService, IUserDataContributor, IUserMerge
{
    public const string InactiveAccount = nameof(InactiveAccount);
    public const string NotOnBoard = nameof(NotOnBoard);
    internal const string BoardPositionSlice = "BoardPosition";

    /// <summary>Only an active account can take a seat; anything else fails with <see cref="InactiveAccount"/>.</summary>
    public async Task<RoleAssignmentResult> AddAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        if (await users.GetUserInfoAsync(userId, ct) is not { IsActive: true } member)
            return new RoleAssignmentResult(false, InactiveAccount);

        var result = await roleWrite.AssignRoleAsync(member.Id, RoleNames.Board, actorUserId, notes: null, ct);
        if (string.Equals(result.ErrorKey, "UserUnavailable", StringComparison.Ordinal))
            return new RoleAssignmentResult(false, InactiveAccount);
        if (result.Success)
            await bodySync.RequestAsync(GovernanceBody.Board, ct);
        return result;
    }

    /// <summary>Ends the active Board seat and drops the office the person held.</summary>
    public async Task<RoleAssignmentResult> EndAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var seat = (await roles.GetByUserIdAsync(userId, ct))
            .FirstOrDefault(a => string.Equals(a.RoleName, RoleNames.Board, StringComparison.Ordinal) && a.IsActive(now));
        if (seat is null)
            return new RoleAssignmentResult(false, "RoleNotActive");

        var result = await roleWrite.EndRoleAsync(seat.Id, actorUserId, notes: null, ct);
        if (result.Success
            && (await repository.GetBoardPositionsAsync(ct)).FirstOrDefault(p => p.UserId == userId) is { } held)
        {
            await repository.ClearBoardPositionForUserAsync(userId, ct);
            await LogPositionAsync(userId, $"Board office {held.Position} dropped as the Board seat ended", actorUserId);
        }

        if (result.Success)
            await bodySync.RequestAsync(GovernanceBody.Board, ct);
        return result;
    }

    /// <summary>Each current Board member's office; someone whose seat ended is left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, BoardPosition>> GetPositionsAsync(CancellationToken ct = default)
    {
        var board = (await roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct)).ToHashSet();
        return (await repository.GetBoardPositionsAsync(ct))
            .Where(p => board.Contains(p.UserId))
            .ToDictionary(p => p.UserId, p => p.Position);
    }

    /// <summary>
    /// Gives an office to a current Board member, who then holds no other; a null user leaves it empty.
    /// Fails with <see cref="NotOnBoard"/> for someone without an active seat.
    /// </summary>
    public async Task<RoleAssignmentResult> SetPositionAsync(
        BoardPosition position, Guid? userId, Guid actorUserId, CancellationToken ct = default)
    {
        if (userId is { } holder && !await roles.IsUserBoardMemberAsync(holder, ct))
            return new RoleAssignmentResult(false, NotOnBoard);

        var rows = await repository.GetBoardPositionsAsync(ct);
        var previousHolder = rows.FirstOrDefault(p => p.Position == position)?.UserId;
        var holderOldOffice = rows.FirstOrDefault(p => p.UserId == userId)?.Position;
        await repository.SetBoardPositionAsync(position, userId, clock.GetCurrentInstant(), ct);

        if (previousHolder is { } previous && previous != userId)
            await LogPositionAsync(previous, $"Board office {position} removed", actorUserId);
        if (userId is { } given && holderOldOffice != position)
        {
            var was = holderOldOffice is { } old ? $" (gave up {old})" : string.Empty;
            await LogPositionAsync(given, $"Board office {position} given{was}", actorUserId);
        }

        return new RoleAssignmentResult(true);
    }

    /// <summary>Empties whatever office the member holds; holding none is a no-op success.</summary>
    public async Task<RoleAssignmentResult> ClearPositionAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var held = (await repository.GetBoardPositionsAsync(ct)).FirstOrDefault(p => p.UserId == userId);
        return held is null
            ? new RoleAssignmentResult(true)
            : await SetPositionAsync(held.Position, null, actorUserId, ct);
    }

    private Task LogPositionAsync(Guid userId, string description, Guid actorUserId) =>
        audit.LogAsync(GovernanceAuditActions.BoardPositionSet, AuditSubjectTypes.User, userId, description, actorUserId);

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        await repository.ReassignBoardPositionAsync(mergedFromUserId, mergedToUserId, now, ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var row = (await repository.GetBoardPositionsAsync(ct)).FirstOrDefault(p => p.UserId == userId);
        return [new UserDataSlice(BoardPositionSlice, row is null ? null : new { row.Position })];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [BoardPositionSlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.ClearBoardPositionForUserAsync(userId, ct);
}
