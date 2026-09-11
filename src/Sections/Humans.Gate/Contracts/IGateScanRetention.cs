using NodaTime;

namespace Humans.Gate.Contracts;

/// <summary>
/// Retention purge seam for the Gate section's append-only scan log. Implemented by the
/// section service; its only consumer is <c>GateRetentionJob</c> in this same project.
/// </summary>
public interface IGateScanRetention
{
    /// <summary>Deletes scan rows older than <paramref name="cutoff"/>; returns how many went.</summary>
    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken cancellationToken = default);
}
