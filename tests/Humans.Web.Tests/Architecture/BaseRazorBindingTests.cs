using AwesomeAssertions;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Every call site of a Base tag helper or view component sits under a <c>_ViewImports.cshtml</c> chain that opens
/// Base's tag helpers.
/// </summary>
/// <remarks>
/// <c>@addTagHelper *, X</c> names an assembly, not a namespace. A missed directive ships the element as inert markup
/// on a green build with no error, which is also why this is a test: that markup never reaches an analyzer.
/// <c>NonceTagHelper</c> is left out because it targets every <c>&lt;script&gt;</c>.
/// </remarks>
public class BaseRazorBindingTests
{
    private const string BaseDirective = "@addTagHelper *, Humans.Base";

    /// <summary>
    /// <c>&lt;vc:human&gt;</c> is spelled with its three terminators so it doesn't also match
    /// <c>&lt;vc:human-search&gt;</c> or <c>&lt;vc:human-summary&gt;</c>.
    /// </summary>
    private static readonly string[] BaseOwnedMarkup =
    [
        "<vc:human ", "<vc:human/", "<vc:human>",
        "<vc:human-search",
        "<vc:access-matrix",
        "<vc:temp-data-alerts",
        "<markdown-editor",
        "<page-header",
        "authorize-policy=",
    ];

    [HumansFact]
    public void EveryBaseTagCallSiteSitsUnderAViewImportsThatBindsBase()
    {
        var src = SrcRoot();

        var callSites = RazorFiles(src)
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .Where(f => BaseOwnedMarkup.Any(m => f.Text.Contains(m, StringComparison.Ordinal)))
            .Select(f => f.Path)
            .ToList();

        callSites.Should().NotBeEmpty(
            "the scan must find the call sites it is guarding — an empty sweep is a broken sweep, "
            + "not a clean bill of health");

        var unbound = callSites.Where(f => !BindsBase(f, src)).ToList();

        unbound.Should().BeEmpty(
            "a Base tag helper or view component whose assembly never opens Humans.Base' tag "
            + "helpers ships as inert literal markup with a green build and no runtime error");
    }

    /// <summary>
    /// Razor silently ignores a directive naming a missing assembly, so its tag helpers ship as inert markup.
    /// </summary>
    [HumansFact]
    public void EveryAddTagHelperDirectiveNamesAnAssemblyThatExists()
    {
        var src = SrcRoot();

        var projects = Directory
            .EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        var directives = RazorFiles(src)
            .Where(f => string.Equals(Path.GetFileName(f), "_ViewImports.cshtml", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(line => (File: f, Line: line)))
            .Select(x => (x.File, Assembly: AssemblyNamedBy(x.Line)))
            .Where(x => x.Assembly is not null)
            .ToList();

        directives.Should().NotBeEmpty(
            "the scan must find the directives it is guarding — an empty sweep is a broken sweep");

        directives
            .Where(x => x.Assembly!.StartsWith("Humans.", StringComparison.Ordinal)
                     && !projects.Contains(x.Assembly))
            .Select(x => $"{x.File} -> {x.Assembly}")
            .Should().BeEmpty(
                "a directive naming an assembly that no longer exists resolves to nothing in "
                + "silence and hides that the real one is missing");
    }

    /// <summary>The assembly in <c>@addTagHelper *, Assembly</c>, or null if the line is not one.</summary>
    private static string? AssemblyNamedBy(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("@addTagHelper ", StringComparison.Ordinal))
            return null;

        var comma = trimmed.LastIndexOf(',');
        return comma < 0 ? null : trimmed[(comma + 1)..].Trim();
    }

    // Razor applies every _ViewImports.cshtml from the project root down to the view's folder.
    private static bool BindsBase(string viewPath, string srcRoot)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(viewPath)!);
             dir is not null && dir.FullName.StartsWith(srcRoot, StringComparison.Ordinal);
             dir = dir.Parent)
        {
            var imports = Path.Combine(dir.FullName, "_ViewImports.cshtml");
            if (File.Exists(imports)
                && File.ReadAllText(imports).Contains(BaseDirective, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> RazorFiles(string srcRoot) =>
        Directory
            .EnumerateFiles(srcRoot, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "src");
    }
}
