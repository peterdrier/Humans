namespace Humans.Issues.Domain;

/// <summary>
/// Issue lifecycle. Submissions land in <see cref="Triage"/>. Terminal states:
/// <see cref="Resolved"/>, <see cref="WontFix"/>, <see cref="Duplicate"/>.
/// A reporter posting a comment on a terminal issue auto-reopens it to <see cref="Open"/>.
/// </summary>
internal enum IssueStatus
{
    Triage,
    Open,
    InProgress,
    Resolved,
    WontFix,
    Duplicate
}

internal static class IssueStatusExtensions
{
    public static bool IsTerminal(this IssueStatus s) =>
        s is IssueStatus.Resolved or IssueStatus.WontFix or IssueStatus.Duplicate;
}
