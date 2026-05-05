using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: controllers don't carry business logic. Action methods
/// over the line threshold OR with cyclomatic complexity ≥ the complexity
/// threshold are flagged.
///
/// Source rule:
/// <c>memory/architecture/no-business-logic-in-controllers.md</c>.
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
/// - Threshold: lines &gt; <see cref="LineThreshold"/> OR complexity ≥
///   <see cref="ComplexityThreshold"/> → violation.
///
/// The seed baseline absorbs the current state. New action methods that
/// breach either threshold trip the ratchet.
///
/// Locator key includes parameter arity (<c>name/arity</c>) so overloads
/// are distinguishable — and edits inside one overload don't require
/// updating the baseline entry for the other.
/// </summary>
public class NoBusinessLogicInControllersRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoBusinessLogicInControllers.baseline.txt";

    // Threshold is intentionally generous for now — the goal is to keep the
    // worst offenders out, not every hiccup. Tighten over time as the
    // baseline ratchets down.
    private const int LineThreshold = 50;
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
            // (name/arity) collisions are theoretically possible (two overloads
            // with the same arity) but extremely rare. Add an ordinal suffix
            // when they happen to keep keys unique.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var match in MethodHeaderRegex.Matches(content).Cast<Match>())
            {
                var methodName = match.Groups["name"].Value;
                // The header regex ends right at the opening "(" of the
                // parameter list. Locate that paren and balance to find arity.
                var openParenIndex = match.Index + match.Length - 1;
                if (openParenIndex < 0 || openParenIndex >= content.Length || content[openParenIndex] != '(')
                {
                    openParenIndex = content.IndexOf('(', match.Index);
                    if (openParenIndex < 0) continue;
                }
                var closeParenIndex = FindMatchingClose(content, openParenIndex, '(', ')');
                if (closeParenIndex < 0) continue;
                var paramSegment = content.Substring(openParenIndex + 1, closeParenIndex - openParenIndex - 1);
                var arity = CountParameters(paramSegment);

                var bodyStart = content.IndexOf('{', closeParenIndex);
                if (bodyStart < 0) continue;
                var bodyEnd = FindMatchingClose(content, bodyStart, '{', '}');
                if (bodyEnd < 0) continue;

                var body = content.Substring(bodyStart, bodyEnd - bodyStart + 1);
                var lineNumber = RatchetTestRunner.LineNumberAt(content, match.Index);

                var lines = CountEffectiveLines(body);
                var complexity = ComputeComplexity(body);

                if (lines > LineThreshold || complexity >= ComplexityThreshold)
                {
                    var key = $"{methodName}/{arity}";
                    counts.TryGetValue(key, out var n);
                    counts[key] = ++n;
                    var keyWithOrdinal = n == 1 ? key : $"{key}#{n}";
                    yield return $"{rel}:{keyWithOrdinal} # L{lineNumber} lines={lines} cc={complexity}";
                }
            }
        }
    }

    private static int CountParameters(string paramSegment)
    {
        var s = paramSegment.Trim();
        if (s.Length == 0) return 0;
        // Count top-level commas (depth 0 in <>, [], (), {}). One more than
        // top-level commas is the parameter count.
        var depth = 0;
        var top = 0;
        foreach (var c in s)
        {
            switch (c)
            {
                case '<':
                case '[':
                case '(':
                case '{':
                    depth++;
                    break;
                case '>':
                case ']':
                case ')':
                case '}':
                    if (depth > 0) depth--;
                    break;
                case ',':
                    if (depth == 0) top++;
                    break;
            }
        }
        return top + 1;
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

    private static int FindMatchingClose(string source, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            var c = source[i];
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
