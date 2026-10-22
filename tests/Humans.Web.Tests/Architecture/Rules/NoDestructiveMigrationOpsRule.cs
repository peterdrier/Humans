using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Web.Tests.Architecture.Ratchet;

namespace Humans.Web.Tests.Architecture.Rules;

/// <summary>
/// Drops in a migration's <c>Up()</c> ship only once the baseline carries their approval
/// (<c>memory/architecture/no-drops-until-prod-verified.md</c>). Keys include the <c>name:</c> and <c>table:</c>
/// arguments, so same-named columns on different tables stay distinct without line numbers.
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
    /// <c>ReadBaseline</c> drops comments, so without this a bare locator would pass. Each block needs
    /// <c>Approval:</c> plus <c>Evidence:</c> (or <c>Pre-existing:</c>) and must name every identifier in its locators,
    /// on token boundaries, so a new entry can't borrow a neighbour's approval. Judging the evidence is for review.
    /// </summary>
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

    /// <summary>The host's migrations plus each section's; a path-keyed sweep must follow every move.</summary>
    internal static IEnumerable<string> MigrationDirectories(string repoRoot)
    {
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

    /// <summary>Brace-matches from the declaration; migration files are simple enough for that. Null if absent.</summary>
    private static string? ExtractMethodBody(string source, string methodName)
    {
        var declRegex = new Regex(
            @"\bvoid\s+" + Regex.Escape(methodName) + @"\s*\(\s*MigrationBuilder\b",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        var declMatch = declRegex.Match(source);
        if (!declMatch.Success) return null;

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
