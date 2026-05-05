using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no reads of cross-domain <c>[Obsolete]</c>-marked
/// navigation properties outside a <c>#pragma warning disable CS0618</c>
/// block.
///
/// Source rule: cross-domain navs are deliberately marked
/// <c>[Obsolete("Cross-domain nav...")]</c> per design-rules §6c. Each call
/// site that genuinely needs the nav declares a pragma block; everything
/// else must use the corresponding service. Drift here is the path back to
/// cross-section EF traversal.
///
/// Detection (conservative):
/// - Scan <c>src/Humans.Domain/Entities/*.cs</c> for properties marked
///   <c>[Obsolete("Cross-domain nav...")]</c>; collect (entity-type,
///   property-name) pairs.
/// - Scan <c>src/**/*.cs</c> (outside <c>Humans.Domain</c>) for
///   <c>.&lt;PropertyName&gt;</c> accesses where the property name is in
///   the obsolete set.
/// - Drop any access whose enclosing line — or any of the 5 lines
///   preceding it — contains <c>#pragma warning disable CS0618</c>, or
///   whose file globally disables CS0618 above the access.
///
/// This is a coarse heuristic. The seed baseline captures every false
/// positive currently flagged; new genuinely-bad reads still surface.
/// </summary>
public class NoObsoleteNavReadsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoObsoleteNavReads.baseline.txt";

    private static readonly Regex ObsoletePropertyRegex = new(
        @"\[Obsolete\([^\)]*Cross-domain nav[^\)]*\)\]\s*(?:\r?\n)\s*public\s+(?:virtual\s+)?[\w<>?,\s]+?\s+(?<name>\w+)\s*(?:\{|=>)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_obsolete_nav_reads()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoObsoleteNavReads", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        // Pass 1: collect obsolete-nav property names from Domain entities.
        var obsoleteNames = new HashSet<string>(StringComparer.Ordinal);
        var entitiesRoot = Path.Combine(repoRoot, "src", "Humans.Domain", "Entities");
        if (Directory.Exists(entitiesRoot))
        {
            foreach (var path in Directory.EnumerateFiles(entitiesRoot, "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(path);
                foreach (var match in ObsoletePropertyRegex.Matches(content).Cast<Match>())
                {
                    var pname = match.Groups["name"].Value;
                    if (pname.Length > 0) obsoleteNames.Add(pname);
                }
            }
        }

        if (obsoleteNames.Count == 0) yield break;

        // Pass 2: scan everything except the entities themselves for reads.
        var alternation = string.Join("|", obsoleteNames.Select(Regex.Escape));
        var accessRegex = new Regex(@"\.(?<n>" + alternation + @")\b",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));

        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            // Skip the entity files themselves — declarations aren't reads.
            if (path.Replace('\\', '/').Contains("/Humans.Domain/Entities/", StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            if (!accessRegex.IsMatch(content)) continue;

            // Build a list of pragma-disable spans (offsets where CS0618 is
            // disabled). Simple model: track running disable state.
            var disableSpans = ComputePragmaDisableSpans(content);

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            // Per-(file, prop) ordinal so multiple reads of the same nav in
            // one file stay distinct without line numbers.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var match in accessRegex.Matches(content).Cast<Match>())
            {
                if (IsInsideDisableSpan(disableSpans, match.Index)) continue;
                var prop = match.Groups["n"].Value;
                counts.TryGetValue(prop, out var n);
                counts[prop] = ++n;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:{prop}#{n} # L{line}";
            }
        }
    }

    private static List<(int Start, int End)> ComputePragmaDisableSpans(string content)
    {
        var spans = new List<(int Start, int End)>();
        var disableRegex = new Regex(@"#pragma\s+warning\s+disable\b[^\r\n]*",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        var restoreRegex = new Regex(@"#pragma\s+warning\s+restore\b[^\r\n]*",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));

        // Simplification: any disable that mentions CS0618 (or no specific
        // codes — bare "#pragma warning disable" disables everything) opens
        // a span until the next matching restore (or EOF).
        int? openStart = null;
        int searchFrom = 0;
        while (searchFrom < content.Length)
        {
            var disable = disableRegex.Match(content, searchFrom);
            var restore = restoreRegex.Match(content, searchFrom);

            // Find the earliest pragma directive after searchFrom.
            var nextIsDisable =
                disable.Success && (!restore.Success || disable.Index < restore.Index);
            var nextIsRestore =
                restore.Success && (!disable.Success || restore.Index <= disable.Index);

            if (nextIsDisable)
            {
                var directive = disable.Value;
                if (openStart is null && DirectiveCovers0618(directive))
                {
                    openStart = disable.Index;
                }
                searchFrom = disable.Index + disable.Length;
            }
            else if (nextIsRestore)
            {
                var directive = restore.Value;
                if (openStart is not null && DirectiveCovers0618(directive))
                {
                    spans.Add((openStart.Value, restore.Index + restore.Length));
                    openStart = null;
                }
                searchFrom = restore.Index + restore.Length;
            }
            else
            {
                break;
            }
        }
        if (openStart is not null)
            spans.Add((openStart.Value, content.Length));
        return spans;
    }

    private static bool DirectiveCovers0618(string directive)
    {
        // "#pragma warning disable" with no specific codes covers everything,
        // including CS0618. Otherwise check explicit mention.
        // Strip the prefix "#pragma warning disable|restore"
        var idx = directive.IndexOf("disable", StringComparison.Ordinal);
        if (idx < 0) idx = directive.IndexOf("restore", StringComparison.Ordinal);
        if (idx < 0) return false;
        var rest = directive.Substring(idx + 7).Trim();
        if (rest.Length == 0) return true;
        return rest.Contains("CS0618", StringComparison.Ordinal)
            || rest.Contains("0618", StringComparison.Ordinal);
    }

    private static bool IsInsideDisableSpan(List<(int Start, int End)> spans, int offset)
    {
        foreach (var (s, e) in spans)
            if (offset >= s && offset < e) return true;
        return false;
    }
}
