using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// "Do the thing", not "give me the rows": retry-vs-permanent turns on <c>GoogleApiException.Error.Code</c>, which
/// stays inside the section.
/// </summary>
public interface IGoogleSyncOutboxProcessor : IApplicationService
{
    /// <summary>One batch. Never throws for a single event: failures are recorded on the row and metered.</summary>
    [ExternalWrite]
    Task ProcessQueuedAsync(CancellationToken cancellationToken = default);
}
