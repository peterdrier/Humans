using System.Runtime.CompilerServices;

namespace Humans.Testing;

/// <summary>
/// Permanently skips a known-broken test; the reason should link the tracking issue. Prefer it over commenting the
/// test out, so it stays discoverable.
/// </summary>
public sealed class BrokenFactAttribute : HumansFactAttribute
{
    public BrokenFactAttribute(
        string reason,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "BrokenFact requires a non-empty reason describing the breakage and where it is tracked.",
                nameof(reason));
        }

        Skip = reason;
    }
}
