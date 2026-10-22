using AwesomeAssertions;

namespace Humans.Web.Tests.Architecture.Ratchet;

/// <summary>Scratch baselines live under the repo's <c>obj/</c>, reachable through the same repo-root resolution.</summary>
public class RatchetTestRunnerTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly string _baselineRelativePath;
    private readonly string _baselineAbsolutePath;
    private bool _disposed;

    public RatchetTestRunnerTests()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        _baselineRelativePath = $"obj/ratchet-tests-scratch/{unique}.baseline.txt";
        _baselineAbsolutePath = Path.Combine(repoRoot, _baselineRelativePath);
        _scratchDir = Path.GetDirectoryName(_baselineAbsolutePath)!;
        Directory.CreateDirectory(_scratchDir);
    }

    [HumansFact]
    public void Run_passes_when_current_violations_match_baseline()
    {
        File.WriteAllLines(_baselineAbsolutePath, [
            "# Test baseline",
            "src/A.cs:10:foo",
            "src/B.cs:20:bar"
        ]);

        RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath, ["src/A.cs:10:foo", "src/B.cs:20:bar"]);
    }

    [HumansFact]
    public void Run_passes_when_baseline_missing_and_no_violations()
    {
        RatchetTestRunner.Run("TestRule", _baselineRelativePath, []);
    }

    [HumansFact]
    public void Run_hard_fails_on_new_violation()
    {
        File.WriteAllLines(_baselineAbsolutePath, [
            "src/A.cs:10:known-violation"
        ]);

        var act = () => RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath, ["src/A.cs:10:known-violation", "src/B.cs:55:NEW-violation"]);

        act.Should().Throw<Exception>()
            .WithMessage("*NEW violation*", "the new line should be flagged");
        act.Should().Throw<Exception>()
            .WithMessage("*src/B.cs:55:NEW-violation*",
                "the failure message must include the offending file:line");
    }

    [HumansFact]
    public void Run_soft_fails_on_stale_baseline_entry()
    {
        File.WriteAllLines(_baselineAbsolutePath, [
            "src/A.cs:10:already-fixed",
            "src/B.cs:20:still-here"
        ]);

        var act = () => RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath, ["src/B.cs:20:still-here"]);

        act.Should().Throw<Exception>()
            .WithMessage("*you fixed*",
                "soft-fail message should congratulate the fixer and ask them to update the baseline");
        act.Should().Throw<Exception>()
            .WithMessage("*src/A.cs:10:already-fixed*",
                "the stale line must appear in the failure message");
    }

    [HumansFact]
    public void ReadBaseline_strips_comments_and_blanks()
    {
        File.WriteAllLines(_baselineAbsolutePath, [
            "# header",
            string.Empty,
            "src/A.cs:1:x",
            "   # indented comment",
            "src/B.cs:2:y"
        ]);
        var read = RatchetTestRunner.ReadBaseline(_baselineRelativePath);
        read.Should().BeEquivalentTo("src/A.cs:1:x", "src/B.cs:2:y");
    }

    [HumansFact]
    public void ReadBaseline_strips_trailing_informational_suffix()
    {
        File.WriteAllLines(_baselineAbsolutePath, [
            "src/A.cs:foo # L42 lines=10",
            "src/B.cs:bar#1 # L99",
            "src/C.cs:baz"
        ]);
        var read = RatchetTestRunner.ReadBaseline(_baselineRelativePath);
        read.Should().BeEquivalentTo("src/A.cs:foo", "src/B.cs:bar#1", "src/C.cs:baz");
    }

    [HumansFact]
    public void StripTrailingComment_preserves_embedded_hash_in_key()
    {
        RatchetTestRunner.StripTrailingComment("src/A.cs:HasOne<User>#2 # L42")
            .Should().Be("src/A.cs:HasOne<User>#2");
        RatchetTestRunner.StripTrailingComment("src/A.cs:Edit/3")
            .Should().Be("src/A.cs:Edit/3");
        RatchetTestRunner.StripTrailingComment("src/A.cs:concurrency-token#1")
            .Should().Be("src/A.cs:concurrency-token#1");
    }

    [HumansFact]
    public void Run_passes_when_only_trailing_line_info_changes()
    {
        File.WriteAllLines(_baselineAbsolutePath, [
            "src/A.cs:Edit/2 # L100 lines=30",
            "src/B.cs:Update/1 # L50 lines=20"
        ]);

        RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath, [
                "src/A.cs:Edit/2 # L142 lines=30",   // shifted +42 lines
                "src/B.cs:Update/1 # L73 lines=22" // shifted +23 lines, diagnostics changed
            ]);
    }

    [HumansFact]
    public void Run_failure_message_includes_full_locator_with_line_info()
    {
        File.WriteAllLines(_baselineAbsolutePath, []);

        var act = () => RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath, ["src/B.cs:NewViolation/1 # L77 lines=42"]);

        act.Should().Throw<Exception>()
            .WithMessage("*src/B.cs:NewViolation/1*");
        act.Should().Throw<Exception>()
            .WithMessage("*L77*",
                "the informational suffix should appear in the failure message even though it doesn't participate in diffing");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (File.Exists(_baselineAbsolutePath)) File.Delete(_baselineAbsolutePath);
            if (Directory.Exists(_scratchDir) && !Directory.EnumerateFileSystemEntries(_scratchDir).Any())
                Directory.Delete(_scratchDir);
        }
        catch (IOException)
        {
            // Best effort: concurrent runs may collide on the delete, and dotnet clean removes obj/ anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
        GC.SuppressFinalize(this);
    }
}
