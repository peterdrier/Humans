using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Google.Services;

/// <summary>
/// "Do the thing", not "give me the rows": retry-vs-permanent turns on <c>GoogleApiException.Error.Code</c>, which
/// stays inside the section.
/// </summary>
internal interface IGoogleSyncOutboxProcessor : IApplicationService
{
    /// <summary>One batch. Never throws for a single event: failures are recorded on the row and metered.</summary>
    [ExternalWrite]
    Task ProcessQueuedAsync(CancellationToken cancellationToken = default);
}
