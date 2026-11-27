using System.Diagnostics;

using Xunit;

namespace Humans.Testing.Tests;

public class SkipAttributeSmokeTest
{
    [HumansFact]
    public void BrokenFact_NullReason_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new BrokenFactAttribute(null!));
        Assert.Contains("non-empty reason", ex.Message, StringComparison.Ordinal);
    }

    [HumansFact]
    public void BrokenFact_EmptyReason_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new BrokenFactAttribute(""));
    }

    [HumansFact]
    public void BrokenFact_WhitespaceReason_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new BrokenFactAttribute("   "));
    }

    [HumansFact]
    public void BrokenFact_Reason_BecomesSkipMessage()
    {
        const string reason = "https://github.com/nobodies-collective/Humans/issues/999";
        var attr = new BrokenFactAttribute(reason);
        Assert.Equal(reason, attr.Skip);
    }

    // The body would fail if run; the skip proves BrokenFact never runs it.
    [BrokenFact("Demo — body must never execute. Tracked at nobodies-collective/Humans#586.")]
    public void BrokenFact_Demo_NeverRuns()
    {
        Assert.Fail("BrokenFact did not skip this test — the skip mechanism is broken.");
    }

    [HumansFact]
    public void IntegrationTestGate_IntegrationSources_Applies()
    {
        Assert.True(IntegrationTestGate.AppliesTo("/repo/tests/Humans.Integration.Tests/Foo.cs"));
    }

    [HumansFact]
    public void IntegrationTestGate_OtherProjects_IsIgnored()
    {
        Assert.False(IntegrationTestGate.AppliesTo("/repo/tests/Humans.Testing.Tests/Foo.cs"));
        Assert.False(IntegrationTestGate.AppliesTo(null));
    }

    [HumansFact]
    public void DebuggerOnlyFact_Constructs()
    {
        var attr = new DebuggerOnlyFactAttribute();
        Assert.NotNull(attr);
    }

    [HumansFact]
    public void DebuggerOnlyFact_HasSkipMessage()
    {
        var attr = new DebuggerOnlyFactAttribute();
        Assert.False(string.IsNullOrWhiteSpace(attr.Skip));
    }

    [DebuggerOnlyFact]
    public void DebuggerOnlyFact_Demo_RunsOnlyUnderDebugger()
    {
        Assert.True(Debugger.IsAttached, "Expected a debugger to be attached.");
    }
}
