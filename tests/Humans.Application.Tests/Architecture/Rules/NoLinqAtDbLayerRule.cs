using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: services don't compose LINQ chains against
/// <c>HumansDbContext</c> / <c>DbSet&lt;T&gt;</c> — repos materialize.
///
/// Source rule: <c>memory/architecture/no-linq-at-db-layer.md</c>. The
/// repository boundary is the only place LINQ-on-EF-entities should live;
/// services receive materialized lists.
///
/// Detection: scan <c>src/Humans.Application/Services/**/*.cs</c> for
/// <c>_db.</c> / <c>_dbContext.</c> / <c>_context.</c> field accesses.
/// (Application services with a <c>HumansDbContext</c> field already breach
/// repository-required-for-db-access; this rule additionally catches the
/// LINQ-composition shape — accesses to <c>.Where</c> / <c>.Select</c> /
/// <c>.OrderBy</c> via a context field.)
/// </summary>
public class NoLinqAtDbLayerRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoLinqAtDbLayer.baseline.txt";

    private static readonly Regex DbFieldAccess = new(
        @"\b(?<f>_db|_dbContext|_context)\.\w+",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_LINQ_at_db_layer_in_application_services()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoLinqAtDbLayer", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        var servicesRoot = Path.Combine(repoRoot, "src", "Humans.Application", "Services");
        if (!Directory.Exists(servicesRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(servicesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            if (!DbFieldAccess.IsMatch(content)) continue;
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            foreach (var match in DbFieldAccess.Matches(content).Cast<Match>())
            {
                var lineNumber = LineNumberAt(content, match.Index);
                yield return $"{rel}:{lineNumber}:{match.Value}";
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
