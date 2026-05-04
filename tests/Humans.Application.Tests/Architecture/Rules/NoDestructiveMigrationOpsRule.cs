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
/// body, emit one locator per line.
///
/// The <c>Down()</c> method legitimately mirrors <c>Up()</c> Adds with Drops,
/// so we strip <c>Down()</c> bodies before scanning.
/// </summary>
public class NoDestructiveMigrationOpsRule
{
    private const string BaselinePath =
        "tests/Humans.Application.Tests/Architecture/Baselines/NoDestructiveMigrationOps.baseline.txt";

    private static readonly Regex DropOpRegex = new(
        @"migrationBuilder\.(?<op>DropColumn|DropTable|DropIndex|DropForeignKey|DropUniqueConstraint|DropCheckConstraint|DropPrimaryKey)\b",
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

            // Walk lines of the Up body and emit one locator per drop call,
            // mapping back to absolute file line numbers.
            foreach (var match in DropOpRegex.Matches(upBody).Cast<Match>())
            {
                // Convert offset-within-Up-body to absolute line in file.
                var upBodyStart = content.IndexOf(upBody, StringComparison.Ordinal);
                var absoluteOffset = upBodyStart + match.Index;
                var lineNumber = LineNumberAt(content, absoluteOffset);
                var op = match.Groups["op"].Value;
                yield return $"{rel}:{lineNumber}:{op}";
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

    private static int LineNumberAt(string source, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }
}
