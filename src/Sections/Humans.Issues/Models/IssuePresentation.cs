using Humans.Issues.Contracts;

namespace Humans.Issues.Models;

internal static class IssuePresentation
{
    /// <summary>Pairs background with explicit foreground so contrast holds in any color scheme.</summary>
    public static string StatusPillClass(IssueStatus s) => s switch
    {
        IssueStatus.Triage => "bg-warning text-dark",
        IssueStatus.Open => "bg-primary text-white",
        IssueStatus.InProgress => "bg-info text-dark",
        IssueStatus.Resolved => "bg-success text-white",
        _ => "bg-secondary text-white"
    };

    public static string CategoryEmoji(IssueCategory c) => c switch
    {
        IssueCategory.Bug => "🐛",
        IssueCategory.Feature => "✨",
        IssueCategory.Question => "❓",
        _ => string.Empty
    };
}
