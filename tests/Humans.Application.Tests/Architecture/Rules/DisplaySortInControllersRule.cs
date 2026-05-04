using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

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
        var reposRoot = Path.Combine(repoRoot, "src", "Humans.Infrastructure", "Repositories");
        if (!Directory.Exists(reposRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(reposRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            if (!SortRegex.IsMatch(content)) continue;
            var lines = content.Split('\n');
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);

            foreach (var match in SortRegex.Matches(content).Cast<Match>())
            {
                var lineNumber = LineNumberAt(content, match.Index);
                var thisLine = lineNumber - 1 < lines.Length ? lines[lineNumber - 1] : string.Empty;
                var prevLine = lineNumber - 2 >= 0 && lineNumber - 2 < lines.Length ? lines[lineNumber - 2] : string.Empty;

                if (thisLine.Contains(AllowMarker, StringComparison.Ordinal)) continue;
                if (prevLine.Contains(AllowMarker, StringComparison.Ordinal)) continue;

                var op = match.Groups["op"].Value;
                yield return $"{rel}:{lineNumber}:{op}";
            }
        }
    }

    private static int LineNumberAt(string source, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }
}
