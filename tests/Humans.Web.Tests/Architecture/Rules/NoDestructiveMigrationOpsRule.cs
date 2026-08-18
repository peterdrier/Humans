using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Web.Tests.Architecture.Ratchet;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no destructive operations in EF migration <c>Up()</c>
/// methods (DropColumn / DropTable / DropIndex / DropForeignKey /
/// DropUniqueConstraint / DropCheckConstraint / DropPrimaryKey).
///
/// Source rule: <c>memory/architecture/no-drops-until-prod-verified.md</c>.
/// Destructive ops are allowed, and this rule is what makes the approval visible: a drop
/// ships once its locator is in the baseline alongside a comment naming what was removed,
/// the evidence it held nothing in use, and Peter's per-case approval. A bare locator with
/// no such comment is the failure mode the baseline header warns about.
///
/// Detection: scan <c>src/Humans.Infrastructure/Migrations/*.cs</c> and each G5 section's
/// <c>src/Sections/Humans.&lt;Section&gt;/Data/Migrations/*.cs</c>
/// (excluding <c>.Designer.cs</c> and <c>*DbContextModelSnapshot.cs</c>),
/// find every <c>migrationBuilder.Drop*</c> call inside the <c>Up</c> method
/// body, emit one locator per call. The <c>name: "X"</c> and (where present)
/// <c>table: "Y"</c> arguments are captured so the same drop op on different
/// objects — including same-named columns on different tables — produces
/// distinct keys without using line numbers.
///
/// The <c>Down()</c> method legitimately mirrors <c>Up()</c> Adds with Drops,
/// so we strip <c>Down()</c> bodies before scanning.
/// </summary>
public class NoDestructiveMigrationOpsRule
{
    private const string BaselinePath =
        "tests/Humans.Web.Tests/Architecture/Baselines/NoDestructiveMigrationOps.baseline.txt";

    private static readonly Regex DropOpRegex = new(
        @"migrationBuilder\.(?<op>DropColumn|DropTable|DropIndex|DropForeignKey|DropUniqueConstraint|DropCheckConstraint|DropPrimaryKey)\b\s*\(\s*(?<args>[^;]*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.Singleline,
        TimeSpan.FromSeconds(2));

    private static readonly Regex NameArgRegex = new(
        @"\bname\s*:\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private static readonly Regex TableArgRegex = new(
        @"\btable\s*:\s*""(?<table>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private static readonly Regex LocatorNameRegex = new(
        @"name=(?<name>[^),]+)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private static readonly Regex LocatorTableRegex = new(
        @"table=(?<table>[^),]+)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_destructive_migration_ops_in_Up()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = ScanMigrations(repoRoot, MigrationDirectories(repoRoot));
        RatchetTestRunner.Run("NoDestructiveMigrationOps", BaselinePath, violations);
    }

    /// <summary>
    /// Every baseline entry carries its approval in the comment block above it.
    /// </summary>
    /// <remarks>
    /// Without this, the ledger is a convention rather than a guardrail: <c>ReadBaseline</c>
    /// discards comments and diffs locators only, so a bare locator with no description, no
    /// evidence and no approval would pass silently — which is the whole gate the rule rewrite
    /// of 2026-08-18 depends on. An <c>Approval:</c> block must also carry an <c>Evidence:</c>
    /// line (<c>Pre-existing:</c> is exempt — shipped history has nothing to prove). This does
    /// not try to judge whether the evidence is *good*; a human does that. It only refuses an
    /// entry that never claimed any. Deliberate deception — fake keywords, false claims — is
    /// out of scope: that is what human review of the baseline diff is for.
    ///
    /// The block must also *name* each dropped object it covers — every identifier in the
    /// locator, the table included, not just the column. Coverage alone is inheritable: a bare
    /// locator appended directly under an approved group (no blank line) would sit inside that
    /// group's comment block and borrow its Approval line, and a column-name-only check would
    /// still let an approval for one table's column cover a same-named column on any other
    /// table. Identifiers match on token boundaries, not substrings, so a block naming
    /// <c>RawPayloadBackup</c> does not cover <c>RawPayload</c>. Requiring every identifier in
    /// the block forces each new entry to put its own names into the approval text — a visible,
    /// deliberate edit — instead of free-riding on a neighbour's.
    /// </remarks>
    [HumansFact]
    public void Every_baseline_entry_is_covered_by_an_approval_note()
    {
        var path = Path.Combine(RatchetTestRunner.LocateRepoRoot(), BaselinePath);
        if (!File.Exists(path)) return;

        var unapproved = new List<string>();
        var block = new List<string>();
        var afterLocator = false;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) { block.Clear(); afterLocator = false; continue; }
            if (line.StartsWith('#'))
            {
                // A comment run starting directly under a locator is a NEW block — reset so
                // its entries cannot combine with the previous group's Approval:/Evidence:.
                if (afterLocator) { block.Clear(); afterLocator = false; }
                block.Add(line);
                continue;
            }
            afterLocator = true;

            var covered = block.Any(c => c.Contains("Pre-existing:", StringComparison.Ordinal))
                || (block.Any(c => c.Contains("Approval:", StringComparison.Ordinal))
                    && block.Any(c => c.Contains("Evidence:", StringComparison.Ordinal)));
            var identifiers = new[]
                {
                    LocatorNameRegex.Match(line).Groups["name"].Value,
                    LocatorTableRegex.Match(line).Groups["table"].Value,
                }
                .Where(id => id.Length > 0);
            var named = identifiers.All(id => block.Any(c => Regex.IsMatch(
                c,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(id)}(?![A-Za-z0-9_])",
                RegexOptions.None,
                TimeSpan.FromSeconds(2))));
            if (!covered || !named) unapproved.Add(line);
        }

        unapproved.Should().BeEmpty(
            because: "every locator in {0} needs the comment block above it to carry an "
                   + "'Approval:' line (Peter's per-case written approval) plus an 'Evidence:' "
                   + "line, or a 'Pre-existing:' line for drops that shipped before this rule, "
                   + "and that block must name the dropped column and table exactly — an entry "
                   + "cannot borrow a neighbouring group's approval. See memory/architecture/"
                   + "no-drops-until-prod-verified.md. Uncovered: {1}",
            BaselinePath, string.Join(", ", unapproved));
    }

    /// <summary>
    /// The host's migrations folder plus each G5 section's own <c>Data/Migrations</c>.
    /// </summary>
    /// <remarks>
    /// The section half is not cosmetic. Scanning only the Base folder means a section's
    /// migrations silently leave the sweep on the day the section moves, and the rule then
    /// reports success by finding nothing — the same failure
    /// <c>DisplaySortInControllersRule</c> hit at Campaigns, on a second path-keyed rule
    /// (nobodies-collective/Humans#866, G5 template step 11: widen the sweep, never shrink
    /// the expectation).
    /// </remarks>
    internal static IEnumerable<string> MigrationDirectories(string repoRoot)
    {
        // Migrations/System followed SystemDbContext to Humans.Web when G5 lane 5b-6 deleted
        // Humans.Infrastructure. Leaving the old path here would drop it from the sweep
        // silently, which is the exact failure the remark above describes.
        var hostMigrations = Path.Combine(repoRoot, "src", "Humans.Web", "Migrations");
        if (Directory.Exists(hostMigrations)) yield return hostMigrations;

        var sections = Path.Combine(repoRoot, "src", "Sections");
        if (!Directory.Exists(sections)) yield break;

        foreach (var sectionMigrations in Directory.EnumerateDirectories(sections)
                     .Select(d => Path.Combine(d, "Data", "Migrations"))
                     .Where(Directory.Exists))
        {
            yield return sectionMigrations;
        }
    }

    internal static IEnumerable<string> ScanMigrations(string repoRoot, IEnumerable<string> migrationDirs)
    {
        // AllDirectories: per-section migration folders (Migrations/<Section>/,
        // nobodies-collective/Humans#858) are in scope too. Every context's
        // snapshot (<Context>ModelSnapshot.cs) is excluded by suffix.
        foreach (var path in migrationDirs
                     .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories)))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".Designer.cs", StringComparison.Ordinal)) continue;
            if (name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)) continue;

            var content = File.ReadAllText(path);
            var upBody = ExtractMethodBody(content, "Up");
            if (upBody is null) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var upBodyStart = content.IndexOf(upBody, StringComparison.Ordinal);
            // Per-key ordinal so multiple drops with the same op+name shape
            // (rare, but possible across schemas) remain distinct.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var match in DropOpRegex.Matches(upBody).Cast<Match>())
            {
                var op = match.Groups["op"].Value;
                var args = match.Groups["args"].Value;
                var nameMatch = NameArgRegex.Match(args);
                var arg = nameMatch.Success ? nameMatch.Groups["name"].Value : "?";
                var tableMatch = TableArgRegex.Match(args);
                var key = tableMatch.Success
                    ? $"{op}(name={arg},table={tableMatch.Groups["table"].Value})"
                    : $"{op}(name={arg})";
                counts.TryGetValue(key, out var n);
                counts[key] = ++n;
                var absoluteOffset = upBodyStart + match.Index;
                var line = RatchetTestRunner.LineNumberAt(content, absoluteOffset);
                yield return $"{rel}:{key}#{n} # L{line}";
            }
        }
    }

    /// <summary>
    /// Extract the body (between matched braces) of the named method from
    /// a migration <c>.cs</c> file. Returns null if the method isn't found.
    /// Migration files are simple enough that brace-matching from the
    /// method declaration is reliable.
    /// </summary>
    private static string? ExtractMethodBody(string source, string methodName)
    {
        // Find "void <methodName>(MigrationBuilder ...".
        var declRegex = new Regex(
            @"\bvoid\s+" + Regex.Escape(methodName) + @"\s*\(\s*MigrationBuilder\b",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        var declMatch = declRegex.Match(source);
        if (!declMatch.Success) return null;

        // From end of declaration, find the opening '{' and balance braces.
        var openBrace = source.IndexOf('{', declMatch.Index);
        if (openBrace < 0) return null;

        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(openBrace, i - openBrace + 1);
            }
        }
        return null;
    }
}
