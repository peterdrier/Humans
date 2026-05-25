using Humans.Application.Interfaces.Mailer.Dtos;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// Splits import into a plan-build step and an apply step. Future
/// Hangfire / webhook callers reuse the same pair: build a plan
/// (possibly single-decision in the webhook case), then apply it.
/// </summary>
public interface IMailerImportService : IApplicationService
{
    Task<ImportPlan> BuildPlanAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies the plan. When <paramref name="maxPerOutcome"/> is set to a
    /// positive value, processes at most that many decisions per
    /// <see cref="Dtos.SubscriberOutcome"/> bucket (in plan order), holding the
    /// rest back. Decisions held back are counted in
    /// <see cref="Dtos.ImportResult.DecisionsThrottled"/>. Null or non-positive
    /// processes the entire plan.
    /// </summary>
    Task<ImportResult> ApplyAsync(
        ImportPlan plan, int? maxPerOutcome = null, CancellationToken ct = default);
}
