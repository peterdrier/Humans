using Humans.Tickets.Services.Dtos;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

/// <summary>
/// Plan, then apply only the selected rows. Apply re-queries, so a sync in between is tolerated (counted as
/// <c>VanishedBetweenPlanAndApply</c>).
/// </summary>
internal interface IAttendeeContactImportService : IApplicationService
{
    Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default);

    Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        Guid actorUserId,
        CancellationToken ct = default);
}
