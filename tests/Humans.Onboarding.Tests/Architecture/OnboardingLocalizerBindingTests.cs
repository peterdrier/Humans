using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;

namespace Humans.Onboarding.Tests.Architecture;

/// <summary>
/// Every resource key the section renders exists in the resource set its call site is bound to.
/// </summary>
/// <remarks>
/// <para>
/// A key looked up against the wrong <c>IStringLocalizer&lt;T&gt;</c> is not an error. The
/// localizer returns the key as its own value, so the page renders the literal
/// <c>Onboarding_BannerText</c> — with a green build, a 200, no log line, and identical
/// behaviour in all six languages, which is what makes it survive a translation review too.
/// The section shipped two of these: the progress banner's two keys were bound to
/// <c>SharedResource</c> after the carve moved them into <c>OnboardingResource</c>, and the
/// review detail page rendered <c>Admin_*</c> labels through the section's own set.
/// </para>
/// <para>
/// <c>OnboardingPageRenderTests</c> is the render-level counterpart, but it cannot be the
/// guard: it asserts the absence of the three carved prefixes, so a key from any other prefix
/// passes, and its banner assertion runs against a fully-onboarded persona, for whom the
/// banner renders nothing at all. This test needs no database and no Docker, and it covers
/// every call site rather than the pages a fixture can reach.
/// </para>
/// <para>
/// Scoped to this section deliberately. The same sweep run repo-wide reports hits in Users and
/// Governance; those belong to their own runs, not this one.
/// </para>
/// </remarks>
public class OnboardingLocalizerBindingTests
{
    /// <summary>Marker type name → the neutral .resx that backs it, relative to <c>src/</c>.</summary>
    private static readonly Dictionary<string, string> ResourceFiles = new(StringComparer.Ordinal)
    {
        ["OnboardingResource"] = "Sections/Humans.Onboarding/OnboardingResource.resx",
        ["ConsentResource"] = "Sections/Humans.Consent/ConsentResource.resx",
        ["SharedResource"] = "Humans.Base/Resources/SharedResource.resx",
    };

    /// <summary><c>@inject IStringLocalizer&lt;X&gt; Name</c> and the ctor-parameter spelling.</summary>
    private static readonly Regex BindingPattern = new(
        @"I(?:Html|String)Localizer<(?<marker>\w+)>\s+(?<name>\w+)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// A <c>name["Key"]</c> lookup. A trailing <c>+</c> means the literal is a prefix being
    /// concatenated with a runtime value (<c>Localizer["Status_" + x]</c>) and the whole key is
    /// not knowable here, so those are skipped rather than reported as missing.
    /// </summary>
    private static readonly Regex LookupPattern = new(
        @"\b(?<name>\w+)\[\s*""(?<key>[A-Za-z][A-Za-z0-9_]*)""\s*(?<next>.)",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void EveryRenderedKeyResolvesInTheSetItsCallSiteBindsTo()
    {
        var src = SrcRoot();
        var sets = ResourceFiles.ToDictionary(
            e => e.Key,
            e => KeysIn(Path.Combine(src, e.Value.Replace('/', Path.DirectorySeparatorChar))),
            StringComparer.Ordinal);

        var resolved = 0;
        var missing = new List<string>();

        foreach (var file in SectionSourceFiles(src))
        {
            var text = File.ReadAllText(file);
            var bindings = BindingsFor(file, src, text);

            foreach (Match lookup in LookupPattern.Matches(text))
            {
                if (lookup.Groups["next"].Value is "+")
                    continue;
                if (!bindings.TryGetValue(lookup.Groups["name"].Value, out var marker)
                    || !sets.TryGetValue(marker, out var keys))
                    continue;

                resolved++;
                var key = lookup.Groups["key"].Value;
                if (!keys.Contains(key))
                    missing.Add($"{Path.GetRelativePath(src, file)}: {marker}[\"{key}\"]");
            }
        }

        resolved.Should().BeGreaterThan(50,
            "the sweep must resolve the section's call sites — a scan that binds nothing "
            + "reports a clean bill of health it never earned");

        missing.Should().BeEmpty(
            "a key missing from the set its call site binds renders as its own name, in every "
            + "language, with no error and a 200");
    }

    /// <summary>
    /// Razor applies every <c>_ViewImports.cshtml</c> from the project root down to the view's
    /// folder, so a view inherits the section-wide <c>@inject</c> lines and may add its own.
    /// C# files carry their bindings as constructor parameters, which appear both bare and with
    /// the primary-constructor field spelling at the use site.
    /// </summary>
    private static Dictionary<string, string> BindingsFor(string file, string src, string text)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);

        if (file.EndsWith(".cshtml", StringComparison.Ordinal))
        {
            foreach (var imports in ViewImportsChain(file, src))
            {
                foreach (Match m in BindingPattern.Matches(File.ReadAllText(imports)))
                    bindings[m.Groups["name"].Value] = m.Groups["marker"].Value;
            }
        }

        foreach (Match m in BindingPattern.Matches(text))
        {
            bindings[m.Groups["name"].Value] = m.Groups["marker"].Value;
            bindings["_" + m.Groups["name"].Value] = m.Groups["marker"].Value;
        }

        return bindings;
    }

    /// <summary>Outermost first, so a nearer <c>_ViewImports</c> overrides a farther one.</summary>
    private static IEnumerable<string> ViewImportsChain(string viewPath, string src)
    {
        var dirs = new List<string>();
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(viewPath)!);
             dir is not null && dir.FullName.StartsWith(src, StringComparison.Ordinal);
             dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
        }

        dirs.Reverse();
        return dirs
            .Select(d => Path.Combine(d, "_ViewImports.cshtml"))
            .Where(File.Exists);
    }

    private static IEnumerable<string> SectionSourceFiles(string src) =>
        Directory
            .EnumerateFiles(Path.Combine(src, "Sections", "Humans.Onboarding"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)
                     || f.EndsWith(".cshtml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static HashSet<string> KeysIn(string resxPath) =>
        XDocument.Load(resxPath)
            .Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "src");
    }
}
