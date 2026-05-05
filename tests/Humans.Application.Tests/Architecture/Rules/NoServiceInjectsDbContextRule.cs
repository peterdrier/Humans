using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no Application service constructor injects
/// <c>HumansDbContext</c> directly.
///
/// Source rule: <c>memory/architecture/repository-required-for-db-access.md</c>.
/// Every DB-accessing service goes through a repository interface; smuggling
/// <c>HumansDbContext</c> into a service is the start of the layer-rule
/// erosion this atom prevents.
///
/// Detection: scan <c>src/Humans.Application/Services/**/*.cs</c> for any
/// constructor signature containing <c>HumansDbContext</c> as a parameter.
/// </summary>
public class NoServiceInjectsDbContextRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoServiceInjectsDbContext.baseline.txt";

    private static readonly Regex CtorParamRegex = new(
        @"\bHumansDbContext\s+\w+",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_application_service_injects_HumansDbContext()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoServiceInjectsDbContext", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        var servicesRoot = Path.Combine(repoRoot, "src", "Humans.Application", "Services");
        if (!Directory.Exists(servicesRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(servicesRoot, "*.cs", SearchOption.AllDirectories))
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
