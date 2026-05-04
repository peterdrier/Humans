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
        catch
        {
            // Test cleanup, swallow.
        }
        GC.SuppressFinalize(this);
    }
}
