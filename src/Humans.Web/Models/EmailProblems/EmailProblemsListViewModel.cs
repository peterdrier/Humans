using Humans.Application.DTOs.EmailProblems;
using NodaTime;

namespace Humans.Web.Models.EmailProblems;

public sealed class EmailProblemsListViewModel
{
    public Instant ScannedAt { get; init; }
    public int TotalProblems => CrossUserConflicts.Count + SingleUserIssues.Count + SystemLevelIssues.Count;

    public IReadOnlyList<CrossUserConflictRow> CrossUserConflicts { get; init; } = Array.Empty<CrossUserConflictRow>();
    public IReadOnlyList<SingleUserIssueRow> SingleUserIssues { get; init; } = Array.Empty<SingleUserIssueRow>();
    public IReadOnlyList<SystemLevelIssueRow> SystemLevelIssues { get; init; } = Array.Empty<SystemLevelIssueRow>();
}

public sealed record CrossUserConflictRow(
    string Email,
    Guid User1Id, string User1DisplayName,
    Guid User2Id, string User2DisplayName);

public sealed record SingleUserIssueRow(
    Guid UserId, string DisplayName,
    IReadOnlyList<string> ProblemSummaries);

public sealed record SystemLevelIssueRow(
    EmailProblemKind Kind,
    Guid? UserEmailId,
    Guid? UserId,
    string Detail);
