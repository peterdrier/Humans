using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no <c>IsConcurrencyToken()</c> /
/// <c>[ConcurrencyCheck]</c> / row-versioning anywhere in live source.
///
/// Source rule: <c>memory/architecture/no-concurrency-tokens.md</c>.
/// Single server, ~500 users — the audit log is the safety net.
///
/// Detection: scan <c>src/**/*.cs</c> (excluding EF migration files —
/// those are auto-generated and reflect the historical model state).
/// Live entity / configuration code with concurrency tokens is the smell.
/// </summary>
public class NoConcurrencyTokensRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoConcurrencyTokens.baseline.txt";

    private static readonly Regex TokenRegex = new(
        @"\.IsConcurrencyToken\s*\(|\.IsRowVersion\s*\(|\[ConcurrencyCheck\]|\[Timestamp\]",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_concurrency_tokens()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoConcurrencyTokens", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            // Skip the migrations directory entirely — historic snapshots and
            // migration .cs files reflect the EF Identity-table model that
            // legitimately includes IsConcurrencyToken on infrastructure
            // tables outside our control.
            if (path.Replace('\\', '/').Contains("/Humans.Infrastructure/Migrations/", StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            if (!TokenRegex.IsMatch(content)) continue;
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in TokenRegex.Matches(content).Cast<Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:concurrency-token#{ordinal} # L{line}";
            }
        }
    }
}
