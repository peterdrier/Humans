using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Gdpr.Services;

/// <summary>
/// Fans out GDPR Article 15 export across <see cref="IUserDataContributor"/>s into one keyed document.
/// Sequential, not Task.WhenAll — a simplicity choice, not a correctness one. The single shared
/// scoped DbContext that once made overlapping contributors unsafe is gone; each section has its
/// own context type now, so no two contributors touch the same instance, and design-rules.md §8a
/// records the old reason as obsolete. One contributor at a time keeps failure attribution and
/// log order plain, and overlapping them would buy nothing at this scale.
/// </summary>
internal sealed class GdprExportService(
    IEnumerable<IUserDataContributor> contributors,
    IClock clock,
    ILogger<GdprExportService> logger) : IGdprExportService
{
    public async Task<GdprExport> ExportForUserAsync(Guid userId, CancellationToken ct = default)
    {
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
                if (slice.Data is null)
                {
                    continue;
                }

                if (sections.ContainsKey(slice.SectionName))
                {
                    // Duplicate section = programming error — fail loudly.
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
            Sections: sections);
    }
}
