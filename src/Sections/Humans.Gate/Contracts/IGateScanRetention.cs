using NodaTime;

namespace Humans.Gate.Contracts;

public interface IGateScanRetention
{
    /// <summary>Returns how many rows went.</summary>
    Task<int> PurgeScansBeforeAsync(Instant cutoff, CancellationToken cancellationToken = default);
}
