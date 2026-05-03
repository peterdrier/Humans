using Humans.Domain.Enums;

namespace Humans.Web.Helpers;

public static class IssuePresentation
{
    /// <summary>
    /// Bootstrap badge color classes for each status. Use as
    /// <c>&lt;span class="badge @IssuePresentation.StatusPillClass(s)"&gt;</c>.
    /// Pairs background with explicit foreground so contrast is guaranteed
    /// regardless of the page's color scheme.
    /// </summary>
    public static string StatusPillClass(IssueStatus s) => s switch
    {
        IssueStatus.Triage => "bg-warning text-dark",
        IssueStatus.Open => "bg-primary text-white",
        IssueStatus.InProgress => "bg-info text-dark",
        IssueStatus.Resolved => "bg-success text-white",
        IssueStatus.WontFix => "bg-secondary text-white",
        IssueStatus.Duplicate => "bg-secondary text-white",
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
