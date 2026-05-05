using System.Text.RegularExpressions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Rules;

/// <summary>
/// Ratcheted rule: no destructive operations in EF migration <c>Up()</c>
/// methods (DropColumn / DropTable / DropIndex / DropForeignKey /
/// DropUniqueConstraint / DropCheckConstraint / DropPrimaryKey).
///
/// Source rule: <c>memory/architecture/no-drops-until-prod-verified.md</c>.
/// Hard storage drops belong in a separate PR after prod soak.
///
/// Detection: scan <c>src/Humans.Infrastructure/Migrations/*.cs</c>
/// (excluding <c>.Designer.cs</c> and <c>HumansDbContextModelSnapshot.cs</c>),
/// find every <c>migrationBuilder.Drop*</c> call inside the <c>Up</c> method
/// body, emit one locator per call. The first <c>name: "X"</c> argument is
/// captured so the same drop op on different objects produces distinct keys
/// without using line numbers.
///
/// The <c>Down()</c> method legitimately mirrors <c>Up()</c> Adds with Drops,
/// so we strip <c>Down()</c> bodies before scanning.
/// </summary>
public class NoDestructiveMigrationOpsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoDestructiveMigrationOps.baseline.txt";

    private static readonly Regex DropOpRegex = new(
        @"migrationBuilder\.(?<op>DropColumn|DropTable|DropIndex|DropForeignKey|DropUniqueConstraint|DropCheckConstraint|DropPrimaryKey)\b\s*\(\s*(?<args>[^;]*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.Singleline,
        TimeSpan.FromSeconds(2));

    private static readonly Regex NameArgRegex = new(
        @"\bname\s*:\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    [HumansFact]
    public void No_new_destructive_migration_ops_in_Up()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var migrationsDir = Path.Combine(repoRoot, "src", "Humans.Infrastructure", "Migrations");
        var violations = ScanMigrations(repoRoot, migrationsDir);
        RatchetTestRunner.Run("NoDestructiveMigrationOps", BaselinePath, violations);
    }

    internal static IEnumerable<string> ScanMigrations(string repoRoot, string migrationsDir)
    {
        if (!Directory.Exists(migrationsDir)) yield break;

        foreach (var path in Directory.EnumerateFiles(migrationsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".Designer.cs", StringComparison.Ordinal)) continue;
            if (name.Equals("HumansDbContextModelSnapshot.cs", StringComparison.Ordinal)) continue;

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
                var key = $"{op}(name={arg})";
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
