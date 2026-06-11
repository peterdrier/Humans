using System.Diagnostics;

using Xunit;

namespace Humans.Domain.Tests;

public class SkipAttributeSmokeTest
{
    // ── BrokenFact construction ───────────────────────────────────────────────

    [HumansFact]
    public void BrokenFact_rejects_null_reason()
    {
        var ex = Assert.Throws<ArgumentException>(() => new BrokenFactAttribute(null!));
        Assert.Contains("non-empty reason", ex.Message, StringComparison.Ordinal);
    }

    [HumansFact]
    public void BrokenFact_rejects_empty_reason()
    {
        Assert.Throws<ArgumentException>(() => new BrokenFactAttribute(""));
    }

    [HumansFact]
    public void BrokenFact_rejects_whitespace_reason()
    {
        Assert.Throws<ArgumentException>(() => new BrokenFactAttribute("   "));
    }

    [HumansFact]
    public void BrokenFact_stores_reason_as_skip_message()
    {
        const string reason = "https://github.com/nobodies-collective/Humans/issues/999";
        var attr = new BrokenFactAttribute(reason);
        Assert.Equal(reason, attr.Skip);
    }

    /// <summary>
    /// This test body would fail if ever executed — the BrokenFact skip
    /// ensures it is never run. Its presence in the suite proves the
    /// skip-in-CI contract.
    /// </summary>
    [BrokenFact("Demo — body must never execute. Tracked at nobodies-collective/Humans#586.")]
    public void BrokenFact_demo_never_executes()
    {
        Assert.Fail("BrokenFact did not skip this test — the skip mechanism is broken.");
    }

    // ── DebuggerOnlyFact construction ─────────────────────────────────────────

    [HumansFact]
    public void DebuggerOnlyFact_constructs_without_error()
    {
        var attr = new DebuggerOnlyFactAttribute();
        Assert.NotNull(attr);
    }

    [HumansFact]
    public void DebuggerOnlyFact_has_non_empty_skip_message()
    {
        var attr = new DebuggerOnlyFactAttribute();
        Assert.False(string.IsNullOrWhiteSpace(attr.Skip));
    }

    /// <summary>
    /// Runs only under a debugger. Asserts <c>Debugger.IsAttached</c> so the
    /// test is self-evidently true when executed intentionally and is skipped
    /// in automated runs.
    /// </summary>
    [DebuggerOnlyFact]
    public void DebuggerOnlyFact_demo_runs_only_under_debugger()
    {
        Assert.True(Debugger.IsAttached, "Expected a debugger to be attached.");
    }
}
