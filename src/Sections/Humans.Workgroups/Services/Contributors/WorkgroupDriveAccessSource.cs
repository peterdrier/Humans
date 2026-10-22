using Humans.Base.Constants;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Workgroups.Domain;

namespace Humans.Workgroups.Services.Contributors;

/// <summary>User ids only, never emails: GoogleIntegration hydrates and filters them uniformly across sources.</summary>
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

            // Active: the members do the work, so they write and organize. Dormant: the folder is the association's
            // record, so it goes read-only.
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

        // Asked about one folder: anything else this source claims isn't this pass's business.
        return claimed.Where(e => folderId is null || string.Equals(e.Key, folderId, StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    /// <summary>Shown in removal emails and the sync log for the root folder.</summary>
    private const string RootFolderName = "Workgroups";

    /// <summary>The Board reads the root; everyone else reaches only their own groups' subfolders.</summary>
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
