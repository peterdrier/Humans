using NodaTime;

namespace Humans.Base.Interfaces;

/// <summary>
/// Data kept for the app-wide retention period and then deleted. The Settings section owns the period and runs one
/// daily job that asks every contributor to delete what is older; a contributor never reads the period itself.
/// </summary>
/// <remarks>In Base because Audit, a contributor, must not reference Settings.</remarks>
public interface IRetentionPeriodContributor : IFanout
{
    /// <summary>What is deleted, in plain words for the settings page and the audit entry, e.g. "temporary audit entries".</summary>
    string Label { get; }

    /// <summary>Deletes every row that happened before <paramref name="cutoff"/>. Returns how many it deleted.</summary>
    Task<int> DeleteOlderThanAsync(Instant cutoff, CancellationToken ct = default);
}
