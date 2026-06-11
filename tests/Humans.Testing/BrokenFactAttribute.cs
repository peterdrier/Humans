using System.Runtime.CompilerServices;

namespace Humans.Testing;

/// <summary>
/// Marks a test as permanently skipped due to a known breakage. The required
/// <paramref name="reason"/> should explain the breakage and reference the
/// tracking issue (e.g. a URL to the relevant issue).
///
/// Prefer this over commenting out a broken test — the test body stays in
/// place and the reason is visible in test output, keeping broken tests
/// discoverable and tracked.
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
