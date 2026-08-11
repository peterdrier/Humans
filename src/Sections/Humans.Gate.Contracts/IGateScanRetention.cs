using NodaTime;

namespace Humans.Gate.Contracts;

/// <summary>
/// Retention purge for the Gate section's append-only scan log. Implemented by the section;
/// called by <c>GateRetentionJob</c>, which stays in <c>Humans.Infrastructure/Jobs</c> because
/// recurring jobs are named by concrete type in Shell's roll-call and have no discovery seam
/// yet (design §15.6b).
/// </summary>
public interface IGateScanRetention
{
    /// <summary>Deletes scan rows older than <paramref name="cutoff"/>; returns how many went.</summary>
    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken cancellationToken = default);
}
