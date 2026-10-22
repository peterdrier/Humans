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
/// Every <c>&lt;vc:...&gt;</c> call site compiles to a tag helper: its component is public and an
/// <c>@addTagHelper</c> above the view opens its assembly.
/// </summary>
/// <remarks>
/// Internal components still resolve at runtime by name, but Razor generates no tag helper for them, so the element
/// ships as literal markup on a green build. It generates no C#, so no analyzer can see it; this covers every page
/// without rendering, unlike <see cref="ViewComponentTagSurvivalMiddleware"/>.
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

    /// <summary>Every view component reachable from Shell's dependency graph, plus Shell and Base themselves.</summary>
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

                // Two components on one tag would be ambiguous; none exist, and picking a winner is not this test's job.
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

    /// <summary>Razor comments are stripped first: <c>_ViewImports</c> files document components inside them.</summary>
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

    private static int LineNumber(string text, int index) =>
        1 + text.AsSpan(0, index).Count('\n');

    /// <summary>Keeps the comments' newlines so line numbers still match the file.</summary>
    private static string StripComments(string text) =>
        RazorComment.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));

    // Razor applies every _ViewImports.cshtml from the project root down. Comments are stripped because some
    // _ViewImports quote the directive inside @* *@, and counting a quote would let the real one be deleted unnoticed.
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
