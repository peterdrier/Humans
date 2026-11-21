using Humans.Google.Contracts;
using Humans.Governance.Data;

namespace Humans.Governance.Services;

/// <summary>
/// Each body's Google group and files hold its roster, as user ids only: Google hydrates and filters the accounts.
/// Never throws, since one throwing source aborts every section's sweep; on a failure the bodies go unclaimed for that
/// pass, and Google leaves unclaimed groups and files alone.
/// </summary>
internal sealed class GovernanceBodyGoogleSource(
    IGovernanceBodyRepository links,
    GovernanceBodyRosters rosters,
    ILogger<GovernanceBodyGoogleSource> logger) : IGoogleGroupMembershipSource, IGoogleDriveAccessSource
{
    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(string? groupKey = null, CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey.Trim();
        var claims = new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var roster = await rosters.GetAsync(ct);
            foreach (var body in await links.GetAllAsync(ct))
            {
                if (body.GoogleGroupEmail is { } email
                    && (requestedKey is null || string.Equals(email, requestedKey, StringComparison.OrdinalIgnoreCase)))
                {
                    claims[email] = [.. roster[body.Body]];
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read the governance bodies' Google groups; leaving them unclaimed this pass");
            claims.Clear();
        }

        return claims;
    }

    public async Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null, CancellationToken ct = default)
    {
        var claims = new Dictionary<string, GoogleDriveAccessClaim>(StringComparer.Ordinal);
        try
        {
            var roster = await rosters.GetAsync(ct);
            foreach (var body in await links.GetAllAsync(ct))
            {
                foreach (var file in body.Files.Where(f => folderId is null || string.Equals(f.GoogleId, folderId, StringComparison.Ordinal)))
                {
                    if (!claims.TryGetValue(file.GoogleId, out var claim))
                        claims[file.GoogleId] = claim = new GoogleDriveAccessClaim([], ResourceName: file.Name);
                    if (file.PermissionLevel == DrivePermissionLevel.None)
                        continue; // still claimed, so reconciliation removes old grants

                    foreach (var userId in roster[body.Body])
                    {
                        if (!claim.Access.TryGetValue(userId, out var level) || file.PermissionLevel > level)
                            claim.Access[userId] = file.PermissionLevel;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read the governance bodies' Google files; leaving them unclaimed this pass");
            claims.Clear();
        }

        return claims;
    }
}
