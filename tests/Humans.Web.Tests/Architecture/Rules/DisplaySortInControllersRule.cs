using System.Text.RegularExpressions;
using Humans.Web.Tests.Architecture.Ratchet;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>
/// Display ordering belongs in the controller, not the repository
/// (<c>memory/architecture/display-sort-in-controllers.md</c>). <c>// arch:db-sort-ok</c> on the sort's line or the
/// line above allows one.
/// </summary>
public class DisplaySortInControllersRule
{
    private const string BaselinePath =
        "tests/Humans.Web.Tests/Architecture/Baselines/DisplaySortInControllers.baseline.txt";

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
            // Per-(file, op) ordinal keeps sorts distinct without line numbers.
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

    private static IEnumerable<string> RepositoryFiles(string repoRoot)
    {
        var sections = Path.Combine(repoRoot, "src", "Sections");
        if (!Directory.Exists(sections))
        {
            throw new DirectoryNotFoundException(
                $"DisplaySortInControllers: expected section root '{sections}' does not exist. " +
                "Every G5 section repository lives under it, so a missing path means the sweep " +
                "scans nothing and the ratchet reads every baseline row as fixed. Update the " +
                "path here rather than letting the rule pass vacuously.");
        }

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
