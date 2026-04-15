using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Gdpr;

/// <summary>
/// Fans out a GDPR Article 15 data export across every registered
/// <see cref="IUserDataContributor"/> and merges their slices into a single
/// document keyed by section name.
///
/// <para>
/// <b>Why sequential, not <c>Task.WhenAll</c>?</b> Every contributor in this
/// codebase uses the scoped <c>HumansDbContext</c> from the current request.
/// <c>DbContext</c> is not thread-safe: two concurrent operations on the same
/// instance throw <c>InvalidOperationException</c>. A naive
/// <c>Task.WhenAll</c> would interleave awaits across contributors and corrupt
/// the shared context. At ~500-user scale, a sequential fan-out is well under
/// a second — parallelism here would be a pure correctness hazard. If a future
/// refactor gives each contributor its own context (via
/// <c>IDbContextFactory</c>) the loop below can become parallel in place.
/// </para>
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
                // Never silently swallow contributor failures — an export that
                // omits a category without warning is worse than an error.
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
                    // Two contributors claiming the same section name is a programming
                    // error — fail loudly rather than silently dropping one slice.
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
