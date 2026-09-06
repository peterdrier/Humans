using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Drains the queued Google sync outbox: one batch, one pass, retries and permanent-failure
/// classification included.
/// </summary>
/// <remarks>
/// Public rather than internal because the Hangfire job that calls it,
/// <c>ProcessGoogleSyncOutboxJob</c>, is public. The contract is deliberately "do the thing"
/// and not "give me the rows": the permanent-vs-retry classification turns on
/// <c>GoogleApiException.Error.Code</c>, which stays inside the section with the
/// <c>Google.Apis.*</c> packages.
/// </remarks>
public interface IGoogleSyncOutboxProcessor : IApplicationService
{
    /// <summary>
    /// Processes up to one batch of queued outbox events. Never throws for an individual
    /// event — per-event failures are recorded on the row and metered.
    /// </summary>
    Task ProcessQueuedAsync(CancellationToken cancellationToken = default);
}
