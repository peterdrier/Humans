using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: application code does not read <c>Profile.IsSuspended</c>.
///
/// Source rule: <c>Profile.IsSuspended</c> is marked obsolete in favour of
/// <c>Profile.State</c> (the lifecycle marker — <c>Stub</c> / <c>Active</c> /
/// <c>Suspended</c>) per the §15i FullProfile landmark (issue #635). The
/// drop-column follow-up is still pending pending prod soak, so existing
/// readers are pinned in the baseline; new readers are blocked.
///
/// Scope (narrow):
/// - Scan <c>src/**/*.cs</c> outside <c>Humans.Domain/Entities/</c>.
/// - Match <c>.IsSuspended</c>. The scanner does not type-resolve — it pins
///   the current set of <c>.IsSuspended</c> reads in a baseline so new
///   reads (which almost certainly target <c>Profile.IsSuspended</c>) fail.
/// </summary>
public class NoProfileIsSuspendedReadsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoProfileIsSuspendedReads.baseline.txt";

    private static readonly Regex IsSuspendedReadRegex = new(
        @"\.IsSuspended\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_profile_issuspended_reads()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoProfileIsSuspendedReads", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            var normalized = path.Replace('\\', '/');

            if (normalized.Contains("/Humans.Domain/Entities/", StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            if (!IsSuspendedReadRegex.IsMatch(content)) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in IsSuspendedReadRegex.Matches(content).Cast<Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:IsSuspended#{ordinal} # L{line}";
            }
        }
    }
}
