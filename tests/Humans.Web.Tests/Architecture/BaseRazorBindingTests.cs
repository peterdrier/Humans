using AwesomeAssertions;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Every call site of a tag helper or view component that lives in Base sits under a
/// <c>_ViewImports.cshtml</c> chain that opens Base's tag helpers.
/// </summary>
/// <remarks>
/// <para>
/// <c>@addTagHelper *, X</c> names an ASSEMBLY, not a namespace, so G5 lane 4b-iii B's move of
/// <c>Humans.UI</c>'s Razor half into <c>Humans.Base</c> (nobodies-collective/Humans#866)
/// had to rewrite 48 directives even though every namespace stayed <c>Humans.UI.*</c>. A missed
/// directive is the worst failure shape in the repo: the element ships as inert literal markup
/// on a green build, with no error, no log line, and a 200 — and the browser silently drops the
/// unknown tag, so the page merely looks like the widget was never there.
/// </para>
/// <para>
/// The render proofs in <c>MovedSharedPartialsRenderTests</c> cover the call sites reachable
/// without fixtures. This covers the rest structurally, and covers whatever call site is added
/// next. It is a test rather than an analyzer because Roslyn does not see <c>.cshtml</c>
/// (peters-hard-rules.md prefers analyzers wherever they can reach), and it lives here rather
/// than in an integration project because it boots nothing and touches no database.
/// </para>
/// <para>
/// <c>NonceTagHelper</c> is deliberately not in the literal list: it targets every
/// <c>&lt;script&gt;</c> element, so a scan for its call sites would flag every view in the
/// tree. Its binding is proved by the CSP nonce reaching rendered script tags instead.
/// </para>
/// </remarks>
public class BaseRazorBindingTests
{
    private const string BaseDirective = "@addTagHelper *, Humans.Base";

    /// <summary>
    /// Literals that only bind through <see cref="BaseDirective"/>. <c>&lt;vc:human&gt;</c> is
    /// spelled with its three possible terminators so it does not also match
    /// <c>&lt;vc:human-search&gt;</c> (Base's too) or <c>&lt;vc:human-summary&gt;</c>
    /// (Humans.Users').
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
    /// No <c>_ViewImports</c> still opens <c>Humans.UI</c>. Lane 5d deleted the project, and
    /// deleting it did NOT turn a leftover directive into a build error — Razor silently
    /// ignores <c>@addTagHelper *, X</c> for an assembly it cannot resolve (measured: adding
    /// the directive back builds with 0 warnings, 0 errors). So the compiler never covers this
    /// and the sweep stays.
    /// </summary>
    [HumansFact]
    public void NoViewImportsStillBindsTheDeletedHumansUiAssembly()
    {
        var stale = RazorFiles(SrcRoot())
            .Where(f => string.Equals(Path.GetFileName(f), "_ViewImports.cshtml", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("@addTagHelper *, Humans.UI", StringComparison.Ordinal))
            .ToList();

        stale.Should().BeEmpty(
            "Humans.UI no longer exists after G5 lane 5d; a directive still naming it resolves to "
            + "nothing in silence and hides that the real one is missing");
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
