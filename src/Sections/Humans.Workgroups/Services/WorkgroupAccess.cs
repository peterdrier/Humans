using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services;

internal sealed class WorkgroupAccess(IWorkgroupRepository repository, IUserService users) : IApplicationService
{
    internal async Task<WorkgroupMutationResult<Workgroup>> RequireAsync(Guid workgroupId, CancellationToken ct)
    {
        var workgroup = await repository.GetWorkgroupAsync(workgroupId, ct);
        return workgroup is null
            ? new(Refusal: new(WorkgroupErrorKeys.NotFound))
            : new(workgroup);
    }

    /// <summary>The group, refused unless it exists and accepts member work.</summary>
    internal async Task<WorkgroupMutationResult<Workgroup>> RequireActiveAsync(Guid workgroupId, CancellationToken ct)
    {
        var result = await RequireAsync(workgroupId, ct);
        if (result.Refusal is null && RequireAcceptsMemberWork(result.Value!) is { } stateRefusal)
            return new(Refusal: stateRefusal);
        return result;
    }

    /// <summary>Member mutations are frozen before registration and once the group ends.</summary>
    internal static WorkgroupRefusal? RequireAcceptsMemberWork(Workgroup w)
    {
        if (w.Status != WorkgroupStatus.Active)
            return new(WorkgroupErrorKeys.Frozen);
        return null;
    }

    internal async Task<WorkgroupMutationResult<Workgroup>> RequireMembershipManagerAsync(
        Guid workgroupId, Guid actorUserId, bool asAdmin, CancellationToken ct)
    {
        var result = await RequireActiveAsync(workgroupId, ct);
        if (result.Refusal is not null) return result;
        if (!asAdmin && !result.Value!.Members.Any(m => m.UserId == actorUserId && m.LeftAt is null && m.IsPending != true
                && m.Role == WorkgroupMemberRole.Coordinator))
        {
            return new(Refusal: new(WorkgroupErrorKeys.NotACoordinator));
        }
        return result;
    }

    internal static WorkgroupRefusal? RequireStatus(Workgroup w, params WorkgroupStatus[] allowed)
    {
        if (!allowed.Contains(w.Status))
            return new(WorkgroupErrorKeys.WrongStatus);
        return null;
    }

    internal async Task<Guid?> ResolveMemberTargetAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.GetUserInfoAsync(userId, ct);
        return user is null || user.IsTombstone ? null : user.Id;
    }

}
