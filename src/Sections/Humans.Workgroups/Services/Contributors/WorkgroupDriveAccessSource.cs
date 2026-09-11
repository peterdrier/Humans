using Humans.Base.Constants;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services.Contributors;

/// <summary>
/// Workgroups' half of GoogleIntegration's Drive access fan-out (design §9). It claims
/// each registered group's own subfolder and the configured root, and says who should hold
/// what on each. GoogleIntegration owns everything else: email hydration, diffing, the
/// mutation, the sync log and reconciliation.
/// </summary>
/// <remarks>
/// User ids only, never emails — the orchestrator hydrates them in one bulk call and
/// applies the user-state filtering uniformly across every source.
/// </remarks>
internal sealed class WorkgroupDriveAccessSource(
    IWorkgroupService workgroups,
    IUserServiceRead users,
    ITeamServiceRead teams) : IGoogleDriveAccessSource
{
    public async Task<Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var claimed = new Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>(StringComparer.Ordinal);
        var register = await workgroups.GetRegisterAsync(ct);

        foreach (var workgroup in register)
        {
            if (workgroup.DriveFolderId is not { Length: > 0 } id)
                continue;

            // Active: the members are doing the work, so they write. Dormant: the folder is
            // the association's record of what they did, so it goes read-only.
            var level = workgroup.Status switch
            {
                WorkgroupStatus.Active => DrivePermissionLevel.Contributor,
                WorkgroupStatus.Dormant => DrivePermissionLevel.Viewer,
                _ => DrivePermissionLevel.None
            };
            // Keep claiming retired folders so reconciliation removes their direct grants.
            claimed[id] = level == DrivePermissionLevel.None
                ? []
                : workgroup.CurrentMemberUserIds().ToDictionary(userId => userId, _ => level);
        }

        if (await workgroups.GetRootDriveFolderIdAsync(ct) is { Length: > 0 } root
            && !claimed.ContainsKey(root))
        {
            claimed[root] = await RootReadersAsync(ct);
        }

        // The orchestrator asked about one folder; anything else this source claims is not
        // its business on this pass.
        return folderId is null
            ? claimed
            : claimed.Where(e => string.Equals(e.Key, folderId, StringComparison.Ordinal))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The root folder is the register's shelf: the Board and every approved Colaborador or
    /// Asociado may read it, and nobody writes to it but the section's own subfolder calls.
    /// </summary>
    private async Task<Dictionary<Guid, DrivePermissionLevel>> RootReadersAsync(CancellationToken ct)
    {
        var readers = new HashSet<Guid>();

        foreach (var user in await users.GetAllUserInfosAsync(ct))
        {
            if (user.Profile is { IsApproved: true, MembershipTier: MembershipTier.Colaborador or MembershipTier.Asociado })
                readers.Add(user.Id);
        }

        if (await teams.GetTeamAsync(SystemTeamIds.Board, ct) is { } board)
        {
            foreach (var member in board.Members)
                readers.Add(member.UserId);
        }

        return readers.ToDictionary(id => id, _ => DrivePermissionLevel.Viewer);
    }
}
