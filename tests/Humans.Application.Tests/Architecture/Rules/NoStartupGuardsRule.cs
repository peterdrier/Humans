using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no startup guards. The Humans app must always boot.
///
/// Source rule: <c>memory/architecture/no-startup-guards.md</c>. A booting
/// app is recoverable; a non-booting app is an outage with no in-app fix.
///
/// Detection: scan <c>src/Humans.Web/Program.cs</c> and adjacent startup
/// extension files for <c>Environment.Exit</c>, <c>Environment.FailFast</c>,
/// and <c>HostApplicationLifetime.StopApplication</c>-style abort calls
/// outside request-handling pipelines. These are unambiguous startup-guard
/// signals.
///
/// Configuration-not-found <c>throw new InvalidOperationException(...)</c>
/// patterns are NOT flagged — they're idiomatic .NET configuration shape
/// validation and the app cannot meaningfully boot without (e.g.) a
/// connection string. The rule targets behavioural guards: "this data
/// looks bad, refuse to start" — which is what hurts.
/// </summary>
public class NoStartupGuardsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoStartupGuards.baseline.txt";

    private static readonly Regex GuardRegex = new(
        @"\bEnvironment\.(?<op>Exit|FailFast)\s*\(|(?<op2>StopApplication)\s*\(",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_startup_guards()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoStartupGuards", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        var webRoot = Path.Combine(repoRoot, "src", "Humans.Web");
        if (!Directory.Exists(webRoot)) yield break;

        // Limit to startup-related code paths (Program.cs + Startup* +
        // Infrastructure/* + Extensions/*). Casting wider catches request-
        // handling guards which are out of scope.
        var candidatePaths = Directory
            .EnumerateFiles(webRoot, "Program.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(webRoot, "Startup*.cs", SearchOption.TopDirectoryOnly))
            .Concat(EnumerateOptional(Path.Combine(webRoot, "Infrastructure")))
            .Concat(EnumerateOptional(Path.Combine(webRoot, "Extensions")));

        foreach (var path in candidatePaths)
        {
            var content = File.ReadAllText(path);
            if (!GuardRegex.IsMatch(content)) continue;
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            foreach (var match in GuardRegex.Matches(content).Cast<Match>())
            {
                var op = match.Groups["op"].Success
                    ? "Environment." + match.Groups["op"].Value
                    : match.Groups["op2"].Value;
                var lineNumber = LineNumberAt(content, match.Index);
                yield return $"{rel}:{lineNumber}:{op}";
            }
        }
    }

    private static IEnumerable<string> EnumerateOptional(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories);
    }

    private static int LineNumberAt(string source, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }
}
