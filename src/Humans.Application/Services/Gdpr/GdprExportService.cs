using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Gdpr;

/// <summary>
/// Fans out GDPR Article 15 export across <see cref="IUserDataContributor"/>s into one keyed document.
/// Sequential, not Task.WhenAll: contributors share the scoped HumansDbContext which is not thread-safe.
/// </summary>
public sealed class GdprExportService(
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
            ExportedAt: clock.GetCurrentInstant().ToInvariantInstantString(),
            Sections: sections);
    }
}
