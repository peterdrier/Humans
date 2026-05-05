using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no controller injects <c>HumansDbContext</c> directly.
///
/// Source rule: <c>docs/architecture/code-review-rules.md</c> +
/// <c>memory/architecture/repository-required-for-db-access.md</c>. Web
/// controllers should call services; services go through repositories.
/// Controllers reaching directly for <c>HumansDbContext</c> bypass both
/// layers.
///
/// Detection: scan <c>src/Humans.Web/Controllers/*.cs</c> for any
/// constructor parameter typed <c>HumansDbContext</c>. Existing exceptions
/// (<c>AdminController</c>, <c>DevLoginController</c>) are baselined.
/// </summary>
public class NoControllerInjectsDbContextRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoControllerInjectsDbContext.baseline.txt";

    private static readonly Regex CtorParamRegex = new(
        @"\bHumansDbContext\s+\w+",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_controller_injects_HumansDbContext()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoControllerInjectsDbContext", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        var controllersRoot = Path.Combine(repoRoot, "src", "Humans.Web", "Controllers");
        if (!Directory.Exists(controllersRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(controllersRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in CtorParamRegex.Matches(content).Cast<Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:HumansDbContext-injected#{ordinal} # L{line}";
            }
        }
    }
}
