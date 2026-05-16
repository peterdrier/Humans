using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no controller declares <c>[Route("Admin/...")]</c> outside
/// the allowlisted <c>AdminController</c> / <c>AdminLegalDocumentsController</c>
/// dashboard. New admin pages always live at <c>/&lt;Section&gt;/Admin/*</c>.
///
/// Source rule: <c>memory/architecture/no-admin-url-section.md</c> — "HARD
/// RULE — /Admin/* is legacy and frozen. No new top-level /Admin/foo routes,
/// controllers, or links can be added to the application going forwards."
///
/// Detection (conservative):
/// - Scan every controller file in <c>src/Humans.Web/Controllers/**/*.cs</c>.
/// - Look for class-level <c>[Route("Admin/&lt;X&gt;")]</c> attributes where
///   <c>&lt;X&gt;</c> is non-empty (i.e. excludes the bare <c>[Route("Admin")]</c>
///   used by the legacy dashboard controllers themselves).
/// - <c>[Route("&lt;Section&gt;/Admin/...")]</c> patterns (Profile/Admin,
///   Tickets/Admin, etc.) are the desired-future-state pattern and are
///   NOT flagged.
/// </summary>
public class NoAdminUrlSectionRoutesRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoAdminUrlSectionRoutes.baseline.txt";

    // Matches `[Route("Admin/<segment>...")]` — captures the route value so
    // we can emit it as the stable key. Excludes the bare `[Route("Admin")]`
    // dashboard prefix by requiring a "/" after "Admin".
    private static readonly Regex AdminRouteRegex = new(
        @"\[Route\s*\(\s*""(?<route>Admin/[^""]+)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_admin_url_section_routes()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = Scan(repoRoot);
        RatchetTestRunner.Run("NoAdminUrlSectionRoutes", BaselinePath, violations);
    }

    internal static IEnumerable<string> Scan(string repoRoot)
    {
        var controllerRoot = Path.Combine(repoRoot, "src", "Humans.Web", "Controllers");
        if (!Directory.Exists(controllerRoot)) yield break;

        foreach (var path in Directory.EnumerateFiles(controllerRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            if (!AdminRouteRegex.IsMatch(content)) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in AdminRouteRegex.Matches(content).Cast<Match>())
            {
                ordinal++;
                var route = match.Groups["route"].Value;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:{route}#{ordinal} # L{line}";
            }
        }
    }
}
