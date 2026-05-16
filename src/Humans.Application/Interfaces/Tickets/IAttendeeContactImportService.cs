using Humans.Application.Interfaces.Tickets.Dtos;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Plan-and-apply import that creates Humans users for ticket attendees
/// whose email doesn't already resolve to an existing user. Mirrors the
/// Mailer import shape: <see cref="BuildPlanAsync"/> classifies, the admin
/// previews + selects, <see cref="ApplyAsync"/> executes only selected rows.
///
/// Stateless: <see cref="ApplyAsync"/> re-queries unmatched attendees so
/// plan and apply are independent (a sync in between is tolerated and
/// counted as <c>VanishedBetweenPlanAndApply</c>).
/// </summary>
public interface IAttendeeContactImportService : IApplicationService
{
    Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default);

    Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan,
        IReadOnlySet<Guid> selectedAttendeeIds,
        Guid actorUserId,
        CancellationToken ct = default);
}
