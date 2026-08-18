using Humans.Application.Interfaces;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Drains the queued Google sync outbox: one batch, one pass, retries and permanent-failure
/// classification included.
/// </summary>
/// <remarks>
/// Here rather than internal because <c>ProcessGoogleSyncOutboxJob</c> sits in the section's
/// <c>Contracts/</c> folder — a separate assembly from this leaf — where it has to be public
/// itself, since recurring jobs are named by concrete type in Shell's
/// <c>UseHumansRecurringJobs</c> roll-call and have no discovery seam yet
/// (G5-SECTION-TEMPLATE.md step 6b). The job used to inject
/// <c>IGoogleSyncOutboxRepository</c> and <c>IGoogleResourceRepository</c> and run the drain
/// itself, which stopped being possible when both repositories became internal to the
/// section. The contract is deliberately "do the thing" and not "give me the rows": the
/// classification it turns on is <c>Google.GoogleApiException.Error.Code</c>, and
/// <c>Humans.Application</c> carries no <c>Google.Apis.*</c> package by design.
/// </remarks>
public interface IGoogleSyncOutboxProcessor : IApplicationService
{
    /// <summary>
    /// Processes up to one batch of queued outbox events. Never throws for an individual
    /// event — per-event failures are recorded on the row and metered.
    /// </summary>
    Task ProcessQueuedAsync(CancellationToken cancellationToken = default);
}
