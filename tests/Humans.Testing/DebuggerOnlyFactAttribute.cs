using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Humans.Testing;

/// <summary>Runs only with a debugger attached; skipped otherwise, so CI stays green.</summary>
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

public static class DebuggerRunCondition
{
    public static bool IsDebuggerAttached => Debugger.IsAttached;
}
