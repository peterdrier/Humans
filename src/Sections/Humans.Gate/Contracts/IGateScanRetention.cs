using NodaTime;

namespace Humans.Gate.Contracts;

/// <summary>
/// Retention purge for the Gate section's append-only scan log. Implemented by the section
/// and, since G5 lane 5b-3 (nobodies-collective/Humans#866), called only from inside it —
/// <c>GateRetentionJob</c> moved to <c>Humans.Gate/Jobs/</c>. This leaf therefore has no
/// consumer outside <c>Humans.Gate</c> at all; folding it into the section's own
/// <c>Contracts/</c> folder and deleting the project is a live follow-up, not done here
/// because retiring a project is Peter's call.
/// </summary>
public interface IGateScanRetention
{
    /// <summary>Deletes scan rows older than <paramref name="cutoff"/>; returns how many went.</summary>
    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken cancellationToken = default);
}
