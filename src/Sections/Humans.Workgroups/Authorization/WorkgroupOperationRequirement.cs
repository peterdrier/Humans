using Microsoft.AspNetCore.Authorization;

namespace Humans.Workgroups.Authorization;

/// <summary>
/// Deliberately no Coordinator operation: the coordinator distinction is register-facing, and the one decision it
/// carries (membership) is checked by the service.
/// </summary>
internal sealed class WorkgroupOperationRequirement : IAuthorizationRequirement
{
    public static readonly WorkgroupOperationRequirement Read = new(nameof(Read));

    /// <summary>Member work: register edits, meetings, log entries, documents, comment responses.</summary>
    public static readonly WorkgroupOperationRequirement Member = new(nameof(Member));

    public static readonly WorkgroupOperationRequirement Administer = new(nameof(Administer));

    private WorkgroupOperationRequirement(string name) => Name = name;

    public string Name { get; }
}
