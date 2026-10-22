using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services.Dtos;

namespace Humans.MailerLite.Services;

internal interface IMailerLiteImportService : IApplicationService
{
    /// <summary>Null before the first run.</summary>
    Task<MailerLiteSyncSnapshot?> GetLastReconciliationAsync(CancellationToken ct = default);

    Task<ImportPlan> BuildPlanAsync(CancellationToken ct = default);

    /// <summary>Builds a fresh plan. <see cref="DriftReport.HumansOptedInMlAbsent"/> stays null.</summary>
    Task<DriftReport> ComputeDriftAsync(CancellationToken ct = default);

    /// <summary>
    /// A positive <paramref name="maxPerOutcome"/> caps each outcome bucket (in plan order) and counts the rest in
    /// <see cref="Dtos.ImportResult.DecisionsThrottled"/>; null or non-positive applies everything.
    /// </summary>
    [ExternalWrite]
    Task<ImportResult> ApplyAsync(
        ImportPlan plan, Guid actorUserId, int? maxPerOutcome = null, CancellationToken ct = default);
}
