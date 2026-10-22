namespace Humans.Issues.Contracts;

/// <summary>
/// Submissions land in <see cref="Triage"/>. Terminal: <see cref="Resolved"/>, <see cref="WontFix"/>,
/// <see cref="Duplicate"/>. A reporter's comment on a terminal issue reopens it to <see cref="Open"/>.
/// </summary>
public enum IssueStatus
{
    Triage,
    Open,
    InProgress,
    Resolved,
    WontFix,
    Duplicate
}

public static class IssueStatusExtensions
{
    public static bool IsTerminal(this IssueStatus s) =>
        s is IssueStatus.Resolved or IssueStatus.WontFix or IssueStatus.Duplicate;
}
