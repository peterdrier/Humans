using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base;
using Humans.Web.Extensions;
using Humans.Web.Tests.Architecture.Ratchet;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Humans.Web.Tests.Resources;

/// <summary>
/// A key with no resx entry renders as the raw key name in every culture, and the parity tests can't see it because
/// they only walk keys the resx already has. Together they make "every culture renders every string" a per-repo
/// guarantee, so a section test never has to restate translated text to prove it.
/// </summary>
public partial class LocalizerKeyResolutionTests
{
    // A literal key closed by ']' or ',' — `Localizer["Prefix_" + x]` is a computed key and out of scope.
    [GeneratedRegex("""\b\w*[Ll]ocalizer\s*\[\s*"(?<key>[^"]+)"\s*[\],]|\.GetString\(\s*"(?<key>[^"]+)"\s*[\),]""",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex LiteralKeyUse();

    [HumansFact]
    public void EveryLiteralLocalizerKeyInSourceExistsInAResxSet()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var known = new[] { typeof(SharedResource) }
            .Concat(SectionDiscoveryExtensions.SectionResourceTypes())
            .SelectMany(t => factory.Create(t).GetAllStrings(includeParentCultures: true))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);

        var src = Path.Combine(RatchetTestRunner.LocateRepoRoot(), "src");
        var uses = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".cshtml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => LiteralKeyUse().Matches(File.ReadAllText(f))
                .Select(m => (Key: m.Groups["key"].Value, File: Path.GetRelativePath(src, f))))
            .ToList();

        // An empty scan or empty key set would pass vacuously.
        known.Should().HaveCountGreaterThan(1000, "the localizer must resolve the shared and section resx sets");
        uses.Should().HaveCountGreaterThan(1000, "the scan must find the literal localizer keys under src/");

        var unresolved = uses
            .Where(u => !known.Contains(u.Key))
            .Select(u => $"{u.Key} ({u.File})")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        unresolved.Should().BeEmpty(
            "every literal localizer key must exist in a base resx, or it renders as its own name; found: "
            + string.Join("; ", unresolved));
    }
}
