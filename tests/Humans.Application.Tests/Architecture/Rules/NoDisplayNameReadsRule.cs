using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: application code does not read <c>User.DisplayName</c> or
/// <c>UserInfo.DisplayName</c>.
///
/// Source rule: <c>memory/architecture/iuserservice-onestop-userinfo.md</c>
/// + <c>memory/architecture/burnername-is-the-display-name.md</c> — display
/// name reads should go through <c>IUserService.GetUserInfosAsync</c> →
/// <c>UserInfo.DisplayName</c>. Direct reads of <c>User.DisplayName</c> (the
/// EF entity property) are a §15 layering smell — they imply a cross-domain
/// nav traversal or a leaked <c>User</c> graph. <c>UserInfo.DisplayName</c>
/// is the cached read model and is the right surface to consume, so reads of
/// it inside the User section's own caching service are fine — only reads
/// from CONTROLLERS / VIEWS / other sections' SERVICES are flagged.
///
/// Scope (intentionally narrow to keep the baseline tractable):
/// - Scan <c>src/**/*.cs</c> outside <c>Humans.Domain/Entities/</c> for
///   <c>.DisplayName</c> accesses.
/// - The scanner does not type-resolve — it flags every <c>.DisplayName</c>
///   it sees. Existing legitimate reads (e.g. <c>TeamInfo.Name</c>,
///   <c>UserInfo.DisplayName</c> in approved consumers) are pinned in the
///   baseline. New occurrences fail the test.
/// </summary>
public class NoDisplayNameReadsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoDisplayNameReads.baseline.txt";

    private static readonly Regex DisplayNameReadRegex = new(
        @"\.DisplayName\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_displayname_reads()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoDisplayNameReads", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            var normalized = path.Replace('\\', '/');

            // Skip entity declarations themselves — they're the source of the
            // property, not a reader.
            if (normalized.Contains("/Humans.Domain/Entities/", StringComparison.Ordinal))
                continue;

            // Skip view-component / view files written as .cs (the .cshtml
            // views are not scanned by RatchetTestRunner.EnumerateSourceFiles
            // since it filters to *.cs already).

            var content = File.ReadAllText(path);
            if (!DisplayNameReadRegex.IsMatch(content)) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in DisplayNameReadRegex.Matches(content).Cast<Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:DisplayName#{ordinal} # L{line}";
            }
        }
    }
}
