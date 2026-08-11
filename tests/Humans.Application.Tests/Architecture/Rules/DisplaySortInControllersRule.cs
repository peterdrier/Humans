using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: display ordering happens in the controller / view-model
/// assembly, not in the repository.
///
/// Source rule:
/// <c>memory/architecture/display-sort-in-controllers.md</c> (new in this PR).
///
/// Detection: scan <c>src/Humans.Infrastructure/Repositories/**/*.cs</c> for
/// <c>.OrderBy(</c> / <c>.OrderByDescending(</c> calls. Honor an inline
/// allow-list comment marker <c>// arch:db-sort-ok</c> on the same line OR
/// the line immediately preceding the call. Everything else is a violation.
/// </summary>
public class DisplaySortInControllersRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/DisplaySortInControllers.baseline.txt";

    private static readonly Regex SortRegex = new(
        @"\.(?<op>OrderBy|OrderByDescending|ThenBy|ThenByDescending)\s*\(",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private const string AllowMarker = "arch:db-sort-ok";

    [HumansFact]
    public void No_new_display_sorts_in_repositories()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("DisplaySortInControllers", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        foreach (var path in RepositoryFiles(repoRoot))
        {
            var content = File.ReadAllText(path);
            if (!SortRegex.IsMatch(content)) continue;
            var lines = content.Split('\n');
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            // Per-(file, op) ordinal so multiple sorts of the same kind in one
            // file stay distinct without line numbers.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var match in SortRegex.Matches(content).Cast<Match>())
            {
                var lineNumber = RatchetTestRunner.LineNumberAt(content, match.Index);
                var thisLine = lineNumber - 1 < lines.Length ? lines[lineNumber - 1] : string.Empty;
                var prevLine = lineNumber - 2 >= 0 && lineNumber - 2 < lines.Length ? lines[lineNumber - 2] : string.Empty;

                if (thisLine.Contains(AllowMarker, StringComparison.Ordinal)) continue;
                if (prevLine.Contains(AllowMarker, StringComparison.Ordinal)) continue;

                var op = match.Groups["op"].Value;
                counts.TryGetValue(op, out var n);
                counts[op] = ++n;
                yield return $"{rel}:{op}#{n} # L{lineNumber}";
            }
        }
    }

    /// <summary>
    /// Every file that owns a repository: Base's <c>Humans.Infrastructure/Repositories</c>
    /// plus each G5 section's <c>Data/</c> folder.
    /// </summary>
    /// <remarks>
    /// The section half is not cosmetic. Scanning only the Base folder means a section's
    /// repository silently leaves the sweep on the day it moves — the rule then reports
    /// success by finding nothing, and the ratchet reads the abandoned baseline rows as
    /// "fixed" while the code is byte-identical. Campaigns is the first moved section that
    /// carried a baseline row, so it is where this surfaced; the rows the widening brings
    /// back are all pre-existing sorts in sections that moved earlier
    /// (nobodies-collective/Humans#866, G5 template step 11: widen the sweep, never shrink
    /// the expectation).
    /// </remarks>
    private static IEnumerable<string> RepositoryFiles(string repoRoot)
    {
        var baseRepos = Path.Combine(repoRoot, "src", "Humans.Infrastructure", "Repositories");
        if (Directory.Exists(baseRepos))
        {
            foreach (var path in Directory.EnumerateFiles(baseRepos, "*.cs", SearchOption.AllDirectories))
            {
                yield return path;
            }
        }

        var sections = Path.Combine(repoRoot, "src", "Sections");
        if (!Directory.Exists(sections)) yield break;

        foreach (var sectionData in Directory.EnumerateDirectories(sections)
                     .Select(d => Path.Combine(d, "Data"))
                     .Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(sectionData, "*.cs", SearchOption.AllDirectories))
            {
                // Generated migrations and the model snapshot are not hand-written queries.
                if (path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return path;
            }
        }
    }
}
