using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services.Dtos;

namespace Humans.MailerLite.Services;

/// <summary>
/// Splits import into a plan-build step and an apply step. Future
/// Hangfire / webhook callers reuse the same pair: build a plan
/// (possibly single-decision in the webhook case), then apply it.
/// </summary>
internal interface IMailerLiteImportService : IApplicationService
{
    /// <summary>
    /// When reconciliation last ran and what it did, or null before the first run.
    /// Read from the section's own table — the dashboard used to scan <c>audit_log</c> for this
    /// (nobodies-collective/Humans#1082).
    /// </summary>
    Task<MailerLiteSyncSnapshot?> GetLastReconciliationAsync(CancellationToken ct = default);

    Task<ImportPlan> BuildPlanAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies the plan. When <paramref name="maxPerOutcome"/> is set to a
    /// positive value, processes at most that many decisions per
    /// <see cref="Dtos.SubscriberOutcome"/> bucket (in plan order), holding the
    /// rest back. Decisions held back are counted in
    /// <see cref="Dtos.ImportResult.DecisionsThrottled"/>. Null or non-positive
    /// processes the entire plan.
    /// </summary>
    [ExternalWrite]
    Task<ImportResult> ApplyAsync(
        ImportPlan plan, int? maxPerOutcome = null, CancellationToken ct = default);
}
