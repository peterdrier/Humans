using Humans.Issues.Contracts;

namespace Humans.Issues.Models;

internal static class IssuePresentation
{
    public static string CategoryEmoji(IssueCategory c) => c switch
    {
        IssueCategory.Bug => "🐛",
        IssueCategory.Feature => "✨",
        IssueCategory.Question => "❓",
        _ => string.Empty
    };
}
