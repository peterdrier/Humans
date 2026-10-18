using Humans.Base.Constants;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
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
    ITeamService teams) : IGoogleDriveAccessSource
{
    public async Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var claimed = new Dictionary<string, GoogleDriveAccessClaim>(StringComparer.Ordinal);
        var register = await workgroups.GetRegisterAsync(ct);

        foreach (var workgroup in register)
        {
            if (workgroup.DriveFolderId is not { Length: > 0 } id)
                continue;

            // Active: the members are doing the work, so they write and organize (move, trash).
            // Dormant: the folder is the association's record of what they did, so it goes read-only.
            var level = workgroup.Status switch
            {
                WorkgroupStatus.Active => DrivePermissionLevel.ContentManager,
                WorkgroupStatus.Dormant => DrivePermissionLevel.Viewer,
                _ => DrivePermissionLevel.None
            };
            // Keep claiming retired folders so reconciliation removes their direct grants.
            var access = level == DrivePermissionLevel.None
                ? []
                : workgroup.CurrentMemberUserIds().ToDictionary(userId => userId, _ => level);
            claimed[id] = new GoogleDriveAccessClaim(access, ResourceName: workgroup.Name);
        }

        if (await workgroups.GetRootDriveFolderIdAsync(ct) is { Length: > 0 } root
            && !claimed.ContainsKey(root))
        {
            claimed[root] = new GoogleDriveAccessClaim(await RootReadersAsync(ct), ResourceName: RootFolderName);
        }

        // The orchestrator asked about one folder; anything else this source claims is not
        // its business on this pass.
        return claimed.Where(e => folderId is null || string.Equals(e.Key, folderId, StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    /// <summary>What a Board member sees named in a removal email or the sync log for the root folder.</summary>
    private const string RootFolderName = "Workgroups";

    /// <summary>
    /// The root folder is the register's shelf: the Board reads it, and nobody writes to it
    /// but the section's own subfolder calls. Everyone else reaches only the subfolders of
    /// the groups they belong to.
    /// </summary>
    private async Task<Dictionary<Guid, DrivePermissionLevel>> RootReadersAsync(CancellationToken ct)
    {
        var readers = new HashSet<Guid>();

        if (await teams.GetTeamAsync(SystemTeamIds.Board, ct) is { } board)
        {
            foreach (var member in board.Members)
                readers.Add(member.UserId);
        }

        return readers.ToDictionary(id => id, _ => DrivePermissionLevel.Viewer);
    }
}
