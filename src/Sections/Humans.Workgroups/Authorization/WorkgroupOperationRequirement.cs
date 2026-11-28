using Microsoft.AspNetCore.Authorization;

namespace Humans.Workgroups.Authorization;

/// <summary>
/// Resource-scoped read, member-work, membership-decision and Board/Admin operations.
/// </summary>
internal sealed class WorkgroupOperationRequirement : IAuthorizationRequirement
{
    public static readonly WorkgroupOperationRequirement Read = new();

    /// <summary>Member work: register edits, meetings, log entries, documents, comment responses.</summary>
    public static readonly WorkgroupOperationRequirement Member = new();

    public static readonly WorkgroupOperationRequirement ManageMembership = new();

    public static readonly WorkgroupOperationRequirement Administer = new();

    private WorkgroupOperationRequirement()
    {
    }
}
