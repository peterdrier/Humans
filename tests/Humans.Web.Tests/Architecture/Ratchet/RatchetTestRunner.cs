using System.Text;
using AwesomeAssertions;

namespace Humans.Web.Tests.Architecture.Ratchet;

/// <summary>
/// Diffs a rule's scan against its baseline by stable key (text before the first <c> # </c>): new violations
/// hard-fail, and fixed ones fail too until removed from the baseline, so a fix-then-regress can't hide behind a
/// stale entry.
/// </summary>
public static class RatchetTestRunner
{
    public static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repository root (no Humans.slnx found walking up from " +
                AppContext.BaseDirectory + ").");
    }

    /// <summary>A missing baseline file reads as empty, so the rule behaves as a hard test.</summary>
    public static SortedSet<string> ReadBaseline(string baselineRelativePath)
    {
        var path = Path.Combine(LocateRepoRoot(), baselineRelativePath);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#')) continue;
            result.Add(StripTrailingComment(trimmed));
        }
        return result;
    }

    /// <summary>
    /// Cuts at the first <c> # </c>; a <c>#</c> with no space before it is part of the key (<c>HasOne&lt;User&gt;#1</c>).
    /// </summary>
    public static string StripTrailingComment(string line)
    {
        var idx = line.IndexOf(" #", StringComparison.Ordinal);
        return idx < 0 ? line : line.Substring(0, idx).TrimEnd();
    }

    /// <summary>Failure messages keep each violation's <c> # …</c> suffix; the diff ignores it.</summary>
    public static void Run(string ruleName, string baselineRelativePath, IEnumerable<string> currentViolations)
    {
        // Several full lines collapsing to one key: first wins.
        var currentByKey = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in currentViolations)
        {
            var key = StripTrailingComment(v);
            if (!currentByKey.ContainsKey(key)) currentByKey.Add(key, v);
        }

        var current = new SortedSet<string>(currentByKey.Keys, StringComparer.Ordinal);
        var baseline = ReadBaseline(baselineRelativePath);

        var newKeys = current.Except(baseline, StringComparer.Ordinal).ToList();
        var fixedKeys = baseline.Except(current, StringComparer.Ordinal).ToList();

        if (newKeys.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Rule '").Append(ruleName).Append("' detected ")
                .Append(newKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" NEW violation(s) not in baseline.\n");
            sb.Append("Baseline file: ").Append(baselineRelativePath).Append('\n');
            sb.Append('\n');
            sb.Append("New violations (fix the code, OR if intentional add these lines to the baseline):\n");
            foreach (var k in newKeys)
                sb.Append("  + ").Append(currentByKey.TryGetValue(k, out var full) ? full : k).Append('\n');
            newKeys.Should().BeEmpty(because: sb.ToString());
        }

        if (fixedKeys.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Rule '").Append(ruleName).Append("': you fixed ")
                .Append(fixedKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" violation(s) — thank you!\n");
            sb.Append("Please remove the following line(s) from the baseline file:\n");
            sb.Append("  ").Append(baselineRelativePath).Append('\n');
            sb.Append('\n');
            foreach (var k in fixedKeys)
                sb.Append("  - ").Append(k).Append('\n');
            sb.Append('\n');
            sb.Append("Why: stale baseline entries silently allow regressions. The ratchet only\n");
            sb.Append("works if baselines shrink as violations are fixed.\n");
            fixedKeys.Should().BeEmpty(because: sb.ToString());
        }
    }

    public static IEnumerable<string> EnumerateSourceFiles(string repoRoot, bool excludeMigrationDesigners = true)
    {
        var srcRoot = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(srcRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (excludeMigrationDesigners)
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".Designer.cs", StringComparison.Ordinal)) continue;
                if (name.EndsWith("DbContextModelSnapshot.cs", StringComparison.Ordinal)) continue;
            }
            yield return path;
        }
    }

    public static string ToRelativePath(string repoRoot, string absolutePath)
    {
        var rel = Path.GetRelativePath(repoRoot, absolutePath);
        return rel.Replace('\\', '/');
    }

    public static int LineNumberAt(string source, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }
}
