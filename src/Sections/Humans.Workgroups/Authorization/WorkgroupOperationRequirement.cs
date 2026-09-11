using Microsoft.AspNetCore.Authorization;

namespace Humans.Workgroups.Authorization;

/// <summary>
/// Resource-based authorization for one group, used as
/// <c>AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.Member)</c> with a
/// <see cref="Services.WorkgroupInfo"/> as the resource.
/// </summary>
/// <remarks>
/// Three operations, because §5 has three answers: everyone reads the register, members do
/// the work, the Board and Admin decide. There is deliberately no Coordinator operation —
/// in v1 the coordinator distinction is register-facing, not permission-facing.
/// </remarks>
internal sealed class WorkgroupOperationRequirement : IAuthorizationRequirement
{
    /// <summary>Read the group page, its published documents, its meetings and its log.</summary>
    public static readonly WorkgroupOperationRequirement Read = new(nameof(Read));

    /// <summary>Member work: register edits, meetings, log entries, documents, comment responses.</summary>
    public static readonly WorkgroupOperationRequirement Member = new(nameof(Member));

    /// <summary>The Secretary's and the Board's decisions.</summary>
    public static readonly WorkgroupOperationRequirement Administer = new(nameof(Administer));

    private WorkgroupOperationRequirement(string name) => Name = name;

    public string Name { get; }
}
