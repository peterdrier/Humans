using System.Diagnostics;

using Xunit;

namespace Humans.Testing.Tests;

public class SkipAttributeSmokeTest
{
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

    // The body would fail if run; the skip proves BrokenFact never runs it.
    [BrokenFact("Demo — body must never execute. Tracked at nobodies-collective/Humans#586.")]
    public void BrokenFact_demo_never_executes()
    {
        Assert.Fail("BrokenFact did not skip this test — the skip mechanism is broken.");
    }

    [HumansFact]
    public void IntegrationTestGate_applies_to_integration_tests_sources()
    {
        Assert.True(IntegrationTestGate.AppliesTo("/repo/tests/Humans.Integration.Tests/Foo.cs"));
    }

    [HumansFact]
    public void IntegrationTestGate_ignores_other_test_projects()
    {
        Assert.False(IntegrationTestGate.AppliesTo("/repo/tests/Humans.Testing.Tests/Foo.cs"));
        Assert.False(IntegrationTestGate.AppliesTo(null));
    }

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

    [DebuggerOnlyFact]
    public void DebuggerOnlyFact_demo_runs_only_under_debugger()
    {
        Assert.True(Debugger.IsAttached, "Expected a debugger to be attached.");
    }
}
