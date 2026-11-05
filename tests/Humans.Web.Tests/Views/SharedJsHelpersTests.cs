using AwesomeAssertions;

namespace Humans.Web.Tests.Views;

/// <summary>
/// site.js owns the antiforgery lookup, HTML escaping and the Chart.js loader; views and section scripts call
/// <c>Humans.*</c> instead of copying them. Source scans, because none of this JS runs under the .NET test host.
/// </summary>
public class SharedJsHelpersTests
{
    private const string ChartJsUrl = "cdn.jsdelivr.net/npm/chart.js@";

    [HumansTheory]
    [InlineData("csrfToken")]
    [InlineData("escapeHtml")]
    [InlineData("postJson")]
    [InlineData("postForm")]
    [InlineData("loadChartJs")]
    public void SiteJs_ExportsHelper(string name)
    {
        var siteJs = File.ReadAllText(Path.Combine(SrcRoot(), "Humans.Web", "wwwroot", "js", "site.js"));

        siteJs.Should().Contain($"{name}: {name}");
    }

    [HumansFact]
    public void ChartJs_IsPinnedInSiteJsOnly()
    {
        var offenders = ViewAndScriptFiles()
            .Where(f => !IsSiteJs(f) && File.ReadAllText(f).Contains(ChartJsUrl, StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty("Humans.loadChartJs() is the one Chart.js loader");
    }

    [HumansFact]
    public void AntiforgeryTokenLookups_LiveInSiteJsOnly()
    {
        var offenders = ViewAndScriptFiles()
            .Where(f => !IsSiteJs(f) && File.ReadAllText(f).Contains("input[name=\"__RequestVerificationToken\"]", StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty("Humans.csrfToken() is the one antiforgery lookup");
    }

    private static bool IsSiteJs(string path) =>
        string.Equals(Path.GetFileName(path), "site.js", StringComparison.Ordinal);

    private static IEnumerable<string> ViewAndScriptFiles() =>
        Directory
            .EnumerateFiles(SrcRoot(), "*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cshtml", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal))
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "src");
    }
}
