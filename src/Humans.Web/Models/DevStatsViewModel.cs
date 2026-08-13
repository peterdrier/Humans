namespace Humans.Web.Models;

/// <summary>
/// Deserialized shape of wwwroot/data/dev-stats.json — the committed snapshot written by
/// scripts/dev-stats.ps1. Nullable at the view: an absent file renders the Engineering
/// section without the numbers panel rather than failing the page.
/// </summary>
public sealed record DevStatsViewModel(
    string GeneratedDate,
    int TotalCommits,
    int MergedPrs,
    int ClosedIssues,
    int TestCount,
    int AnalyzerRuleCount,
    int SectionCount,
    IReadOnlyList<DevStatsContributor> Contributors,
    int ClaudeCoauthoredCommitPercent);

public sealed record DevStatsContributor(string Name, int Commits, int LinesAdded, int LinesDeleted);
