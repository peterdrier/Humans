using Humans.Application.Tests.Architecture.Rules;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Ratchet;

/// <summary>
/// One-shot baseline-seeder. Gated by environment variable
/// <c>HUMANS_SEED_RATCHET_BASELINES=1</c>. Writes/overwrites every rule's
/// baseline file from the current scan output.
///
/// Run via:
///   <c>HUMANS_SEED_RATCHET_BASELINES=1 dotnet test tests/Humans.Application.Tests --filter Seed_all_baselines -v quiet</c>
///
/// Without the env var the test is a silent no-op so it doesn't churn
/// baselines on every CI run.
/// </summary>
public class BaselineSeeder
{
    [HumansFact]
    public void Seed_all_baselines()
    {
        var enabled = Environment.GetEnvironmentVariable("HUMANS_SEED_RATCHET_BASELINES");
        if (!string.Equals(enabled, "1", StringComparison.Ordinal)) return;

        var repoRoot = RatchetTestRunner.LocateRepoRoot();

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoCrossSectionEfJoins.baseline.txt",
            NoCrossSectionEfJoinsRule.ScanConfigurations(repoRoot),
            "no-cross-section EF joins (memory/architecture/no-cross-section-ef-joins.md)");

        var migrationsDir = Path.Combine(repoRoot, "src", "Humans.Infrastructure", "Migrations");
        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoDestructiveMigrationOps.baseline.txt",
            NoDestructiveMigrationOpsRule.ScanMigrations(repoRoot, migrationsDir),
            "no destructive migration ops in Up() (memory/architecture/no-drops-until-prod-verified.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoServiceInjectsDbContext.baseline.txt",
            NoServiceInjectsDbContextRule.Scan(repoRoot),
            "no Application service injects HumansDbContext (memory/architecture/repository-required-for-db-access.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoControllerInjectsDbContext.baseline.txt",
            NoControllerInjectsDbContextRule.Scan(repoRoot),
            "no controller injects HumansDbContext (docs/architecture/code-review-rules.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoConcurrencyTokens.baseline.txt",
            NoConcurrencyTokensRule.Scan(repoRoot),
            "no concurrency tokens (memory/architecture/no-concurrency-tokens.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoLinqAtDbLayer.baseline.txt",
            NoLinqAtDbLayerRule.Scan(repoRoot),
            "no LINQ at DB layer in Application services (memory/architecture/no-linq-at-db-layer.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoStartupGuards.baseline.txt",
            NoStartupGuardsRule.Scan(repoRoot),
            "no startup guards (memory/architecture/no-startup-guards.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoObsoleteNavReads.baseline.txt",
            NoObsoleteNavReadsRule.Scan(repoRoot),
            "no [Obsolete] cross-domain nav reads (design-rules §6c)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/DisplaySortInControllers.baseline.txt",
            DisplaySortInControllersRule.Scan(repoRoot),
            "display sort belongs in controllers (memory/architecture/display-sort-in-controllers.md)");

        WriteBaseline(
            repoRoot,
            "tests/Humans.Application.Tests/Architecture/Baselines/NoBusinessLogicInControllers.baseline.txt",
            NoBusinessLogicInControllersRule.Scan(repoRoot),
            "no business logic in controllers (memory/architecture/no-business-logic-in-controllers.md)");
    }

    private static void WriteBaseline(string repoRoot, string relativePath, IEnumerable<string> violations, string ruleSummary)
    {
        var path = Path.Combine(repoRoot, relativePath);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);

        // Strip any trailing " # …" diagnostic suffix the rule attached
        // (line numbers, lines/cc, etc.) before writing — the baseline file
        // stores stable keys ONLY so unrelated edits never cause baseline
        // churn in git. The runner still receives the rich strings at
        // scan time and uses them to build informative failure messages.
        var sorted = new SortedSet<string>(
            violations.Select(RatchetTestRunner.StripTrailingComment),
            StringComparer.Ordinal);
        var lines = new List<string>
        {
            "# Baseline: " + ruleSummary,
            "# One locator per line: relative/path.cs:stable-key",
            "# Stable keys only — line numbers and other line-volatile diagnostics",
            "# are deliberately omitted so unrelated edits don't churn this file in git.",
            "# Generated by BaselineSeeder.Seed_all_baselines (HUMANS_SEED_RATCHET_BASELINES=1).",
            "# When violations are FIXED, remove the corresponding line from this file.",
            "# When the test reports new violations, fix the code — do not add lines to silence it.",
            string.Empty,
        };
        lines.AddRange(sorted);
        File.WriteAllLines(path, lines);
    }
}
