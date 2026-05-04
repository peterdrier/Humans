using System.Text;
using AwesomeAssertions;

namespace Humans.Application.Tests.Architecture.Ratchet;

/// <summary>
/// Shared scan + diff logic for ratcheted architecture rules.
///
/// **The ratchet pattern:** each rule has a baseline file listing the
/// currently-allowed violations (one locator per line, sorted, comments
/// allowed via <c>#</c>). The test scans the live source tree, diffs against
/// the baseline, and:
///
/// - <b>Hard-fails</b> when new violations appear (lines in the scan not in
///   the baseline). The failure message lists every new line so the offending
///   change can be located immediately.
/// - <b>Soft-fails</b> when the baseline contains lines no longer present in
///   the scan (a violation got fixed). The failure message says "you fixed N —
///   please remove these lines from the baseline file" with the specific
///   lines listed. This keeps baselines from going stale silently.
///
/// Why two separate failure modes: the goal is to ratchet baselines DOWN over
/// time. Silent stale baselines defeat the whole point — a fixed-then-broken
/// regression would slip past as a "still in baseline" line. Forcing the
/// baseline to drop when a violation is fixed makes the ratchet visible in
/// every fix PR.
///
/// Locator format (one per line):
///   <c>relative/path.cs:line:symbol-or-message</c>
///
/// Comments (<c>#</c>) and blank lines are stripped on read.
/// </summary>
public static class RatchetTestRunner
{
    /// <summary>
    /// Walk up from the test bin directory to the repository root (the
    /// directory containing <c>Humans.slnx</c>). Returns the absolute path.
    /// </summary>
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

    /// <summary>
    /// Read a baseline file into a sorted set of locator strings. Comments
    /// (lines starting with <c>#</c>) and blank lines are stripped. If the
    /// baseline file does not exist, returns an empty set — the rule then
    /// behaves as a hard test (any violation fails).
    /// </summary>
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
            result.Add(trimmed);
        }
        return result;
    }

    /// <summary>
    /// Run a ratcheted rule. Compares the rule's current scan output against
    /// its baseline file; hard-fails on new violations, soft-fails (separately)
    /// on stale baseline entries.
    /// </summary>
    /// <param name="ruleName">Human-readable name, used in failure messages.</param>
    /// <param name="baselineRelativePath">Path to the baseline file relative
    /// to the repository root, e.g. <c>tests/Humans.Application.Tests/Architecture/Baselines/MyRule.baseline.txt</c>.</param>
    /// <param name="currentViolations">The locator strings produced by the
    /// rule's scanner against the current tree.</param>
    public static void Run(string ruleName, string baselineRelativePath, IEnumerable<string> currentViolations)
    {
        var current = new SortedSet<string>(currentViolations, StringComparer.Ordinal);
        var baseline = ReadBaseline(baselineRelativePath);

        var newViolations = current.Except(baseline, StringComparer.Ordinal).ToList();
        var fixedViolations = baseline.Except(current, StringComparer.Ordinal).ToList();

        // Hard-fail FIRST — new violations matter more than stale baselines.
        if (newViolations.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Rule '").Append(ruleName).Append("' detected ")
                .Append(newViolations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" NEW violation(s) not in baseline.\n");
            sb.Append("Baseline file: ").Append(baselineRelativePath).Append('\n');
            sb.Append('\n');
            sb.Append("New violations (fix the code, OR if intentional add these lines to the baseline):\n");
            foreach (var v in newViolations)
                sb.Append("  + ").Append(v).Append('\n');
            newViolations.Should().BeEmpty(because: sb.ToString());
        }

        // Soft-fail: baseline contains lines no longer present in the scan.
        // This still fails the test — the baseline must shrink to match
        // reality so future regressions can't hide behind stale entries.
        if (fixedViolations.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Rule '").Append(ruleName).Append("': you fixed ")
                .Append(fixedViolations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" violation(s) — thank you!\n");
            sb.Append("Please remove the following line(s) from the baseline file:\n");
            sb.Append("  ").Append(baselineRelativePath).Append('\n');
            sb.Append('\n');
            foreach (var v in fixedViolations)
                sb.Append("  - ").Append(v).Append('\n');
            sb.Append('\n');
            sb.Append("Why: stale baseline entries silently allow regressions. The ratchet only\n");
            sb.Append("works if baselines shrink as violations are fixed.\n");
            fixedViolations.Should().BeEmpty(because: sb.ToString());
        }
    }

    /// <summary>
    /// Enumerate <c>.cs</c> files under <c>src/</c>, optionally excluding
    /// EF migration designer/snapshot files (auto-generated and outside the
    /// architectural rules' scope).
    /// </summary>
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
                if (name.Equals("HumansDbContextModelSnapshot.cs", StringComparison.Ordinal)) continue;
            }
            yield return path;
        }
    }

    /// <summary>
    /// Convert an absolute path to a forward-slash repo-root-relative path
    /// suitable for use in baseline locator strings.
    /// </summary>
    public static string ToRelativePath(string repoRoot, string absolutePath)
    {
        var rel = Path.GetRelativePath(repoRoot, absolutePath);
        return rel.Replace('\\', '/');
    }
}
