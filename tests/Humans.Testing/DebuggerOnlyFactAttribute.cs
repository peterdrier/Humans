using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Humans.Testing;

/// <summary>
/// Marks a test that should only run when a debugger is attached. When no
/// debugger is present the test is reported as skipped, keeping the suite
/// green in CI while still allowing the test to be exercised by hand.
///
/// Intended for harness and diagnostic tests you run intentionally under a
/// debugger rather than in the automated suite.
/// </summary>
public sealed class DebuggerOnlyFactAttribute : HumansFactAttribute
{
    private const string SkipMessage = "This test only runs under a debugger (attach one to execute it).";

    public DebuggerOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        SkipType = typeof(DebuggerRunCondition);
        SkipUnless = nameof(DebuggerRunCondition.IsDebuggerAttached);
        Skip = SkipMessage;
    }
}

/// <summary>
/// Condition type for <see cref="DebuggerOnlyFactAttribute"/>.
/// xUnit v3 reads <see cref="IsDebuggerAttached"/> at runtime to decide
/// whether to run or skip the test.
/// </summary>
public static class DebuggerRunCondition
{
    public static bool IsDebuggerAttached => Debugger.IsAttached;
}
