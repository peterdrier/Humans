using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture.Ratchet;

/// <summary>
/// Unit tests for <see cref="RatchetTestRunner"/> — verify the
/// hard-fail / soft-fail behavior the ratchet pattern depends on.
///
/// These cover the framework, not any individual rule. Each test
/// creates a temporary baseline file under the system temp dir and
/// drives <c>RatchetTestRunner.Run</c> against it via a relative path
/// reachable from the repo root by going through a per-test scratch
/// directory placed under the repo's <c>obj/</c>.
/// </summary>
public class RatchetTestRunnerTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly string _baselineRelativePath;
    private readonly string _baselineAbsolutePath;
    private bool _disposed;

    public RatchetTestRunnerTests()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        // Place scratch baseline inside obj/ — gitignored, won't pollute the
        // repo, and reachable through the same repo-root resolution path.
        var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
        _baselineRelativePath = $"obj/ratchet-tests-scratch/{unique}.baseline.txt";
        _baselineAbsolutePath = Path.Combine(repoRoot, _baselineRelativePath);
        _scratchDir = Path.GetDirectoryName(_baselineAbsolutePath)!;
        Directory.CreateDirectory(_scratchDir);
    }

    [HumansFact]
    public void Run_passes_when_current_violations_match_baseline()
    {
        File.WriteAllLines(_baselineAbsolutePath, new[]
        {
            "# Test baseline",
            "src/A.cs:10:foo",
            "src/B.cs:20:bar",
        });

        // Act+Assert: should not throw.
        RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath,
            new[] { "src/A.cs:10:foo", "src/B.cs:20:bar" });
    }

    [HumansFact]
    public void Run_passes_when_baseline_missing_and_no_violations()
    {
        // Baseline file does not exist → empty baseline → no violations is fine.
        RatchetTestRunner.Run("TestRule", _baselineRelativePath, Array.Empty<string>());
    }

    [HumansFact]
    public void Run_hard_fails_on_new_violation()
    {
        File.WriteAllLines(_baselineAbsolutePath, new[]
        {
            "src/A.cs:10:known-violation",
        });

        var act = () => RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath,
            new[] { "src/A.cs:10:known-violation", "src/B.cs:55:NEW-violation" });

        act.Should().Throw<Exception>()
            .WithMessage("*NEW violation*", "the new line should be flagged");
        act.Should().Throw<Exception>()
            .WithMessage("*src/B.cs:55:NEW-violation*",
                "the failure message must include the offending file:line");
    }

    [HumansFact]
    public void Run_soft_fails_on_stale_baseline_entry()
    {
        File.WriteAllLines(_baselineAbsolutePath, new[]
        {
            "src/A.cs:10:already-fixed",
            "src/B.cs:20:still-here",
        });

        var act = () => RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath,
            new[] { "src/B.cs:20:still-here" });

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
        File.WriteAllLines(_baselineAbsolutePath, new[]
        {
            "# header",
            string.Empty,
            "src/A.cs:1:x",
            "   # indented comment",
            "src/B.cs:2:y",
        });
        var read = RatchetTestRunner.ReadBaseline(_baselineRelativePath);
        read.Should().BeEquivalentTo(new[] { "src/A.cs:1:x", "src/B.cs:2:y" });
    }

    [HumansFact]
    public void ReadBaseline_strips_trailing_informational_suffix()
    {
        // Trailing " # ..." on data lines is informational only — the diff
        // key stops at the " # " separator. Lines without the separator are
        // returned verbatim.
        File.WriteAllLines(_baselineAbsolutePath, new[]
        {
            "src/A.cs:foo # L42 lines=10",
            "src/B.cs:bar#1 # L99",
            "src/C.cs:baz",
        });
        var read = RatchetTestRunner.ReadBaseline(_baselineRelativePath);
        read.Should().BeEquivalentTo(new[]
        {
            "src/A.cs:foo",
            "src/B.cs:bar#1",
            "src/C.cs:baz",
        });
    }

    [HumansFact]
    public void StripTrailingComment_preserves_embedded_hash_in_key()
    {
        // The key may itself contain a "#N" ordinal (no preceding space) —
        // those must NOT be treated as the start of a comment.
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
        // The whole point of the new format: an unrelated edit that shifts
        // a violation's line number must NOT trip the ratchet.
        File.WriteAllLines(_baselineAbsolutePath, new[]
        {
            "src/A.cs:Edit/2 # L100 lines=30",
            "src/B.cs:Update/1 # L50 lines=20",
        });

        // Same keys, different line numbers and diagnostics — should pass.
        RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath,
            new[]
            {
                "src/A.cs:Edit/2 # L142 lines=30",   // shifted +42 lines
                "src/B.cs:Update/1 # L73 lines=22",  // shifted +23 lines, diagnostics changed
            });
    }

    [HumansFact]
    public void Run_failure_message_includes_full_locator_with_line_info()
    {
        File.WriteAllLines(_baselineAbsolutePath, Array.Empty<string>());

        var act = () => RatchetTestRunner.Run(
            "TestRule",
            _baselineRelativePath,
            new[] { "src/B.cs:NewViolation/1 # L77 lines=42" });

        // The failure message should preserve the trailing info so the
        // human can locate the violation in the source.
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
            // Test scratch cleanup is best-effort; concurrent test runs may
            // collide on the directory delete and that's harmless. Production
            // code logs; this is a per-test sentinel under obj/ which gets
            // cleaned by `dotnet clean` regardless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: scratch is gitignored, leaks fail loudly on next CI run.
        }
        GC.SuppressFinalize(this);
    }
}
