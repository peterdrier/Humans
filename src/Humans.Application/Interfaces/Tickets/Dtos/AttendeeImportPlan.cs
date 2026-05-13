namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// Output of <see cref="Humans.Application.Interfaces.Tickets.IAttendeeContactImportService.BuildPlanAsync"/>.
/// Stateless — the apply step re-queries to defend against concurrent sync mutation.
/// </summary>
public sealed record AttendeeImportPlan(
    IReadOnlyList<AttendeeImportDecision> Decisions,
    int TotalUnmatched)
{
    public AttendeeImportPlanCounts Counts => new(
        AttachVerified: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.AttachVerified),
        AmbiguousMultipleVerified: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.AmbiguousMultipleVerified),
        DeleteUnverifiedThenCreate: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.DeleteUnverifiedThenCreate),
        CreateNewUser: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.CreateNewUser),
        SkipNoEmail: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.SkipNoEmail),
        SkipVoided: Decisions.Count(d => d.Outcome == AttendeeImportOutcome.SkipVoided));
}

public sealed record AttendeeImportPlanCounts(
    int AttachVerified,
    int AmbiguousMultipleVerified,
    int DeleteUnverifiedThenCreate,
    int CreateNewUser,
    int SkipNoEmail,
    int SkipVoided);
