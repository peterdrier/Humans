using Humans.Application.Interfaces;
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
    Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken ct = default);
}
