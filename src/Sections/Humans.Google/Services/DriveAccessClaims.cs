using Humans.Google.Contracts;
using Humans.Google.Data;
using Humans.Teams.Contracts;

namespace Humans.Google.Services;

internal static class DriveAccessClaims
{
    /// <summary>
    /// One source at a time, so a throwing source is skipped; a folder several sources claim gets the union, highest
    /// level per user. A failure marks every claim partial: grant, never remove or demote.
    /// <paramref name="rethrowSourceFailures"/> propagates the failure instead, for callers that must retry.
    /// </summary>
    internal static async Task<ClaimLoad> LoadClaimsAsync(
        IEnumerable<IGoogleDriveAccessSource> sources, string? folderId, ILogger logger, CancellationToken ct,
        bool rethrowSourceFailures = false)
    {
        var claims = new List<(string FolderId, GoogleDriveAccessClaim Claim)>();
        var partial = false;

        foreach (var source in sources)
        {
            Dictionary<string, GoogleDriveAccessClaim> expected;
            try
            {
                expected = await source.GetExpectedAccessAsync(folderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !rethrowSourceFailures)
            {
                logger.LogError(ex,
                    "Google Drive access source {SourceType} threw while computing expected access; skipping this source",
                    source.GetType().Name);
                partial = true;
                continue;
            }

            foreach (var (key, access) in expected)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                claims.Add((key.Trim(), access));
            }
        }

        return new ClaimLoad(claims
            .GroupBy(c => c.FolderId, StringComparer.Ordinal)
            .Select(g => new FolderClaim(g.Key, Union(g.Select(c => c.Claim).ToList()), [], partial))
            .ToList(), partial);
    }

    private static GoogleDriveAccessClaim Union(IReadOnlyList<GoogleDriveAccessClaim> claims)
    {
        if (claims.Count == 1)
            return claims[0];

        var access = new Dictionary<Guid, DrivePermissionLevel>();
        var memberLinks = new Dictionary<Guid, IReadOnlyList<TeamLink>>();
        foreach (var claim in claims)
        {
            foreach (var (userId, level) in claim.Access)
                access[userId] = access.TryGetValue(userId, out var held) && held > level ? held : level;
            foreach (var (userId, links) in claim.MemberTeamLinks ?? new Dictionary<Guid, IReadOnlyList<TeamLink>>())
                memberLinks[userId] = memberLinks.TryGetValue(userId, out var known) ? [.. known, .. links] : links;
        }

        return new GoogleDriveAccessClaim(access, claims.SelectMany(c => c.LinkedTeams ?? []).ToList(), memberLinks,
            claims.Select(c => c.ResourceName).FirstOrDefault(n => n is not null));
    }

    internal sealed record ClaimLoad(IReadOnlyList<FolderClaim> Claims, bool Partial);

    internal sealed record FolderClaim(string FolderId,
        GoogleDriveAccessClaim Claim, IReadOnlyList<TeamGoogleResource> Resources, bool Partial = false, string? Path = null)
    {
        public Dictionary<Guid, DrivePermissionLevel> Access => Claim.Access;
        public Guid ResourceId => Resources.FirstOrDefault()?.Id ?? Guid.Empty;

        /// <summary>Registered resource name (its full path), else the path Drive gave, else the source's name, else the raw file id.</summary>
        public string DisplayName => Resources.FirstOrDefault()?.Name ?? Path ?? Claim.ResourceName ?? FolderId;
    }
}
