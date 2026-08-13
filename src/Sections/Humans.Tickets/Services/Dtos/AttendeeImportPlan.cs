namespace Humans.Tickets.Services.Dtos;

/// <summary>
/// Output of <c>IAttendeeContactImportService.BuildPlanAsync</c>.
/// Stateless — the apply step re-queries to defend against concurrent sync mutation.
/// </summary>
internal sealed record AttendeeImportPlan(
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

internal sealed record AttendeeImportPlanCounts(
    int AttachVerified,
    int AmbiguousMultipleVerified,
    int DeleteUnverifiedThenCreate,
    int CreateNewUser,
    int SkipNoEmail,
    int SkipVoided);
