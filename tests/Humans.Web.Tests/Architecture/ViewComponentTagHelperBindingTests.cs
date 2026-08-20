using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// Every <c>&lt;vc:...&gt;</c> call site in the tree resolves to a tag helper call at compile
/// time: its view component is public, and its assembly's tag helpers are opened by an
/// <c>@addTagHelper</c> directive somewhere in the folder chain above the view.
/// </summary>
/// <remarks>
/// <para>
/// Razor's tag-helper generation requires the component type to be public. A section's
/// <c>internal</c> types resolve fine at RUNTIME via
/// <see cref="SectionViewComponentFeatureProvider"/> (<c>Component.InvokeAsync("Name")</c>),
/// but nothing relaxes the compile-time check, so an internal component — or a missing
/// directive — never gets a tag helper generated. The element then ships to the browser as
/// literal markup: green build, no exception, no log line (nobodies-collective/Humans#1055;
/// caught in production by a human on <c>&lt;vc:profile-card&gt;</c> after
/// <c>ProfileCardViewComponent</c> became internal during the Users G5 move,
/// nobodies-collective/Humans#866, PR peterdrier/Humans#1297).
/// </para>
/// <para>
/// This is a test rather than an analyzer because Roslyn does not see <c>.cshtml</c>
/// (peters-hard-rules.md prefers analyzers wherever they can reach). It boots nothing and
/// touches no database, and it covers every page deterministically without rendering any of
/// them — including pages no browser has ever hit, which
/// <see cref="ViewComponentTagSurvivalMiddleware"/> (the Dev/QA response-scanning middleware
/// that also guards #1055) cannot see until someone requests that page.
/// </para>
/// <para>
/// The view-component set is discovered by reflection over every assembly with a component in
/// it — the same discovery <see cref="SectionViewComponentFeatureProvider"/> itself uses —
/// never a hand-maintained literal list, so a new or renamed component is covered
/// automatically.
/// </para>
/// </remarks>
public class ViewComponentTagHelperBindingTests
{
    private const string ViewComponentSuffix = "ViewComponent";
    private const string CallSiteMarker = "<vc:";

    [HumansFact]
    public void EveryVcCallSiteResolvesToAPublicComponentWhoseAssemblyIsImported()
    {
        var src = SrcRoot();
        var components = DiscoverViewComponents();

        components.Should().NotBeEmpty(
            "the scan must find the view components it is guarding — an empty set is a broken sweep");

        var callSites = RazorFiles(src).SelectMany(CallSites).ToList();

        callSites.Should().NotBeEmpty(
            "the scan must find <vc:...> call sites to check — an empty sweep is a broken sweep, "
            + "not a clean bill of health");

        var failures = new List<string>();

        foreach (var (path, tag, line) in callSites)
        {
            if (!components.TryGetValue(tag, out var component))
            {
                failures.Add($"{path}:{line}: <vc:{tag}> does not name any known view component — it can never resolve");
                continue;
            }

            if (!component.IsPublic)
            {
                failures.Add(
                    $"{path}:{line}: <vc:{tag}> names {component.TypeName}, which is not public — Razor "
                    + "never generates a tag helper call for it, so it ships as inert literal markup");
                continue;
            }

            if (!ImportsAssembly(path, src, component.AssemblyName))
            {
                failures.Add(
                    $"{path}:{line}: <vc:{tag}> is not covered by an @addTagHelper *, {component.AssemblyName} "
                    + "directive in its folder chain — it ships as inert literal markup");
            }
        }

        failures.Should().BeEmpty(because: string.Join(Environment.NewLine, failures));
    }

    private sealed record ViewComponentInfo(string TypeName, string AssemblyName, bool IsPublic);

    /// <summary>
    /// Every view component reachable from Shell's own dependency graph plus Shell and Base
    /// themselves, which are not sections. Assemblies with none contribute nothing and are
    /// skipped implicitly.
    /// </summary>
    private static Dictionary<string, ViewComponentInfo> DiscoverViewComponents()
    {
        var assemblies = SectionDiscoveryExtensions.SectionAssemblies()
            .Concat([typeof(SectionActivation).Assembly, typeof(Humans.Base.ViewComponents.HumanViewComponent).Assembly])
            .Distinct()
            .ToList();

        var components = new Dictionary<string, ViewComponentInfo>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!IsViewComponentType(type))
                    continue;

                // A collision would mean two components resolve to the same tag ambiguously;
                // none exist today. Keeping the first is deliberately conservative — silently
                // picking a winner is not this test's job.
                components.TryAdd(
                    ComponentTagName(type),
                    new ViewComponentInfo(type.FullName ?? type.Name, assembly.GetName().Name!, type.IsPublic));
            }
        }

        return components;
    }

    /// <summary>Mirrors <see cref="SectionViewComponentFeatureProvider"/>'s own component test.</summary>
    private static bool IsViewComponentType(Type type)
    {
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            return false;

        if (type.IsDefined(typeof(NonViewComponentAttribute)))
            return false;

        return type.Name.EndsWith(ViewComponentSuffix, StringComparison.Ordinal)
            || type.IsDefined(typeof(ViewComponentAttribute));
    }

    private static string ComponentTagName(Type type)
    {
        var attributeName = type.GetCustomAttribute<ViewComponentAttribute>()?.Name;
        if (!string.IsNullOrEmpty(attributeName))
            return ToKebabCase(attributeName);

        var shortName = type.Name.EndsWith(ViewComponentSuffix, StringComparison.Ordinal)
            ? type.Name[..^ViewComponentSuffix.Length]
            : type.Name;

        return ToKebabCase(shortName);
    }

    /// <summary>ASP.NET Core's own PascalCase-to-tag-name convention: <c>ProfileCard</c> -&gt; <c>profile-card</c>.</summary>
    private static string ToKebabCase(string pascalCase)
    {
        var builder = new StringBuilder(pascalCase.Length + 4);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c) && i > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static readonly Regex RazorComment = new(
        @"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Every distinct <c>&lt;vc:name</c> call site in one view, tag name plus its 1-based line
    /// number. Razor comments are stripped first — <c>_ViewImports.cshtml</c> files routinely
    /// document a component's binding inside a <c>@* ... *@</c> block (e.g. "invoked by name,
    /// not a tag helper"), and that documentation is not a real call site.
    /// </summary>
    private static IEnumerable<(string Path, string Tag, int Line)> CallSites(string path)
    {
        var text = StripComments(File.ReadAllText(path));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = text.IndexOf(CallSiteMarker, StringComparison.Ordinal);

        while (index >= 0)
        {
            var start = index + CallSiteMarker.Length;
            var end = start;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '-'))
                end++;

            var tag = text[start..end];
            if (tag.Length > 0 && seen.Add(tag))
                yield return (path, tag, LineNumber(text, index));

            index = text.IndexOf(CallSiteMarker, end, StringComparison.Ordinal);
        }
    }

    /// <summary>The 1-based line number of <paramref name="index"/> within <paramref name="text"/>.</summary>
    private static int LineNumber(string text, int index) =>
        1 + text.AsSpan(0, index).Count('\n');

    /// <summary>
    /// Strips <c>@* ... *@</c> comments, keeping every newline the comment contained so line
    /// numbers computed on the result still match the original file.
    /// </summary>
    private static string StripComments(string text) =>
        RazorComment.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));

    // Razor applies every _ViewImports.cshtml from the project root down to the view's folder.
    // Comments are stripped first for the same reason call sites are: several _ViewImports
    // files quote the exact `@addTagHelper *, Humans.Base` text inside a `@* ... *@` block to
    // explain why the directive is there (e.g. Humans.Email's), and a quoted directive binds
    // nothing — counting it would let the real directive be deleted with this test still green.
    private static bool ImportsAssembly(string viewPath, string srcRoot, string assemblyName)
    {
        var directive = $"@addTagHelper *, {assemblyName}";
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(viewPath)!);
             dir is not null && dir.FullName.StartsWith(srcRoot, StringComparison.Ordinal);
             dir = dir.Parent)
        {
            var imports = Path.Combine(dir.FullName, "_ViewImports.cshtml");
            if (File.Exists(imports) && ActiveDirectives(imports).Contains(directive, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string ActiveDirectives(string importsPath) =>
        StripComments(File.ReadAllText(importsPath));

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
