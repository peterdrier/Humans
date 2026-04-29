using Humans.Domain.Enums;

namespace Humans.Web.Helpers;

public static class IssuePresentation
{
    public static string StatusPillClass(IssueStatus s) => s switch
    {
        IssueStatus.InProgress => "status-progress",
        IssueStatus.WontFix => "status-wontfix",
        _ => $"status-{s.ToString().ToLowerInvariant()}"
    };

    public static string CategoryEmoji(IssueCategory c) => c switch
    {
        IssueCategory.Bug => "🐛",
        IssueCategory.Feature => "✨",
        IssueCategory.Question => "❓",
        _ => string.Empty
    };
}
