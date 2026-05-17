using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Gdpr;

/// <summary>
/// Fans out GDPR Article 15 export across <see cref="IUserDataContributor"/>s into one keyed document.
/// Sequential, not Task.WhenAll: contributors share the scoped HumansDbContext which is not thread-safe.
/// </summary>
public sealed class GdprExportService : IGdprExportService
{
    private readonly IEnumerable<IUserDataContributor> _contributors;
    private readonly IClock _clock;
    private readonly ILogger<GdprExportService> _logger;

    public GdprExportService(
        IEnumerable<IUserDataContributor> contributors,
        IClock clock,
        ILogger<GdprExportService> logger)
    {
        _contributors = contributors;
        _clock = clock;
        _logger = logger;
    }

    public async Task<GdprExport> ExportForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var sections = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var contributor in _contributors)
        {
            IReadOnlyList<UserDataSlice> slices;
            try
            {
                slices = await contributor.ContributeForUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                // Never swallow: omitting a category silently is worse than failing.
                _logger.LogError(
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
                    _logger.LogError(
                        "GDPR export has duplicate section {SectionName} from contributor {Contributor}",
                        slice.SectionName,
                        contributor.GetType().Name);
                    throw new InvalidOperationException(
                        $"Duplicate GDPR export section '{slice.SectionName}' returned by {contributor.GetType().Name}.");
                }

                sections[slice.SectionName] = slice.Data;
            }
        }

        _logger.LogInformation(
            "User {UserId} exported their data ({SectionCount} sections)",
            userId,
            sections.Count);

        return new GdprExport(
            ExportedAt: _clock.GetCurrentInstant().ToInvariantInstantString(),
            Sections: sections);
    }
}
