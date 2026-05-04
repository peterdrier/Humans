using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: controllers don't carry business logic. Action methods
/// over ~25 effective lines OR with cyclomatic complexity ≥ 6 are flagged.
///
/// Source rule:
/// <c>memory/architecture/no-business-logic-in-controllers.md</c> (new in
/// this PR).
///
/// Detection (regex-based heuristic — conservative, baseline-friendly):
/// - Scan <c>src/Humans.Web/Controllers/*.cs</c> (or any
///   <c>src/Humans.Web/**/*Controller.cs</c>).
/// - Find methods with action-like signatures: <c>public</c>, returning
///   <c>IActionResult</c> / <c>Task&lt;IActionResult&gt;</c> /
///   <c>ActionResult&lt;T&gt;</c> / <c>Task&lt;ActionResult&lt;T&gt;&gt;</c>.
/// - Count effective lines (non-blank, non-brace-only) inside the method body.
/// - Approximate cyclomatic complexity by counting branch tokens:
///   <c>if</c>, <c>else if</c>, <c>case</c>, <c>&amp;&amp;</c>, <c>||</c>,
///   <c>?</c> (ternary), <c>while</c>, <c>for</c>, <c>foreach</c>,
///   <c>catch</c>. Start at 1.
/// - Threshold: lines &gt; 25 OR complexity ≥ 6 → violation.
///
/// The seed baseline absorbs the current state. New action methods that
/// breach either threshold trip the ratchet.
/// </summary>
public class NoBusinessLogicInControllersRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoBusinessLogicInControllers.baseline.txt";

    private const int LineThreshold = 25;
    private const int ComplexityThreshold = 6;

    [HumansFact]
    public void No_new_business_logic_in_controllers()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoBusinessLogicInControllers", BaselinePath, violations);
    }

    // public [virtual|override|async] [Task<...>|Task|...] Name(...)
    private static readonly Regex MethodHeaderRegex = new(
        @"public\s+(?:virtual\s+|override\s+|async\s+)*(?:Task\s*<[^>]+>|Task|IActionResult|ActionResult\s*<[^>]+>|ActionResult|JsonResult|FileResult|RedirectToActionResult|RedirectResult|ContentResult|ViewResult|PartialViewResult|StatusCodeResult|OkObjectResult|NotFoundResult|BadRequestResult|UnauthorizedResult|EmptyResult)\s+(?<name>[A-Z]\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private static readonly string[] BranchTokens =
    {
        @"\bif\b", @"\bcase\b", @"&&", @"\|\|", @"\bwhile\b", @"\bfor\b",
        @"\bforeach\b", @"\bcatch\b",
    };
    private static readonly Regex TernaryRegex = new(@"\?[^?:]*:",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        var controllersRoot = Path.Combine(repoRoot, "src", "Humans.Web", "Controllers");
        if (!Directory.Exists(controllersRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(controllersRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (!path.EndsWith("Controller.cs", StringComparison.Ordinal)
                && !path.EndsWith("ControllerBase.cs", StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);

            foreach (var match in MethodHeaderRegex.Matches(content).Cast<Match>())
            {
                var methodName = match.Groups["name"].Value;
                var bodyStart = content.IndexOf('{', match.Index + match.Length);
                if (bodyStart < 0) continue;

                var bodyEnd = FindMatchingClose(content, bodyStart);
                if (bodyEnd < 0) continue;

                var body = content.Substring(bodyStart, bodyEnd - bodyStart + 1);
                var lineNumber = LineNumberAt(content, match.Index);

                var lines = CountEffectiveLines(body);
                var complexity = ComputeComplexity(body);

                if (lines > LineThreshold || complexity >= ComplexityThreshold)
                {
                    yield return $"{rel}:{lineNumber}:{methodName} (lines={lines}, cc={complexity})";
                }
            }
        }
    }

    private static int CountEffectiveLines(string methodBody)
    {
        // Strip leading and trailing braces, then count non-empty,
        // non-brace-only lines.
        var inner = methodBody.Trim();
        if (inner.StartsWith('{')) inner = inner.Substring(1);
        if (inner.EndsWith('}')) inner = inner.Substring(0, inner.Length - 1);
        var lines = inner.Split('\n');
        var count = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (string.Equals(line, "{", StringComparison.Ordinal)
                || string.Equals(line, "}", StringComparison.Ordinal)) continue;
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            count++;
        }
        return count;
    }

    private static int ComputeComplexity(string methodBody)
    {
        var cc = 1;
        foreach (var token in BranchTokens)
            cc += Regex.Matches(methodBody, token, RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2)).Count;
        cc += TernaryRegex.Matches(methodBody).Count;
        return cc;
    }

    private static int FindMatchingClose(string source, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int LineNumberAt(string source, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }
}
