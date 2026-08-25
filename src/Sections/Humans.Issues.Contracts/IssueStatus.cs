namespace Humans.Issues.Contracts;

/// <summary>
/// Issue lifecycle. Submissions land in <see cref="Triage"/>. Terminal states:
/// <see cref="Resolved"/>, <see cref="WontFix"/>, <see cref="Duplicate"/>.
/// A reporter posting a comment on a terminal issue auto-reopens it to <see cref="Open"/>.
/// On the leaf beside <see cref="IssueCategory"/> because the Backdoor machine API filters
/// and sets it.
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
