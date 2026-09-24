using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Gdpr.Services;

/// <summary>
/// Fans both halves of GDPR subject rights out across <see cref="IUserDataContributor"/>s:
/// Article 15 export into one keyed document, and Article 17 erasure in turn. Sequential,
/// not Task.WhenAll — a simplicity choice, not a correctness one, which keeps failure
/// attribution and log order plain.
/// </summary>
internal sealed class GdprService(
    IEnumerable<IUserDataContributor> contributors,
    IUserServiceRead users,
    IClock clock,
    ILogger<GdprService> logger) : IGdprService
{
    public async Task<GdprExport> ExportForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // Resolved once, here, rather than per contributor: the envelope names the surviving
        // account and the ids merged into it, so a slice keyed to an archived id reads as this
        // person's row instead of a stranger's. Contributors still resolve for themselves —
        // each one decides whether its rows follow the chain or moved with the merge.
        var info = await users.GetUserInfoAsync(userId, ct);

        var sections = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var contributor in contributors)
        {
            IReadOnlyList<UserDataSlice> slices;
            try
            {
                slices = await contributor.ContributeForUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                // Never swallow: omitting a category silently is worse than failing.
                logger.LogError(
                    ex,
                    "GDPR export contributor {Contributor} failed for user {UserId}",
                    contributor.GetType().Name,
                    userId);
                throw;
            }

            foreach (var slice in slices)
            {
                if (!contributor.ErasureDeclaration.ContainsKey(slice.SectionName))
                {
                    logger.LogError(
                        "GDPR export section {SectionName} from contributor {Contributor} has no erasure declaration",
                        slice.SectionName,
                        contributor.GetType().Name);
                }

                if (slice.Data is null)
                {
                    continue;
                }

                if (sections.ContainsKey(slice.SectionName))
                {
                    logger.LogError(
                        "GDPR export has duplicate section {SectionName} from contributor {Contributor}",
                        slice.SectionName,
                        contributor.GetType().Name);
                    throw new InvalidOperationException(
                        $"Duplicate GDPR export section '{slice.SectionName}' returned by {contributor.GetType().Name}.");
                }

                sections[slice.SectionName] = slice.Data;
            }
        }

        logger.LogInformation(
            "User {UserId} exported their data ({SectionCount} sections)",
            userId,
            sections.Count);

        return new GdprExport(
            ExportedAt: clock.GetCurrentInstant().ToIso8601(),
            UserId: info?.Id ?? userId,
            MergedFromUserIds: info?.MergedUserIds ?? [],
            Sections: sections);
    }

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // The ErasesLast contributor (the identity owner) runs after everything that
        // still needs the person's addresses.
        var ordered = contributors
            .OrderBy(c => c.ErasesLast ? 1 : 0);

        foreach (var contributor in ordered)
        {
            try
            {
                await contributor.EraseForUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                // Never swallow: leaving a section's data behind silently is the bug this exists to kill.
                logger.LogError(ex,
                    "GDPR erasure contributor {Contributor} failed for user {UserId}",
                    contributor.GetType().Name, userId);
                throw;
            }
        }
    }
}
