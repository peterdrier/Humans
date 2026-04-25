using System.Runtime.CompilerServices;
using Xunit;

namespace Humans.Testing;

public sealed class HumansTheoryAttribute : TheoryAttribute
{
    public HumansTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        base.Timeout = 5000;
    }

    public new int Timeout
    {
        get => base.Timeout;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentException(
                    "HumansTheory requires a positive timeout in milliseconds. Infinite timeout is forbidden by project policy. Use [HumansTheory(Timeout = N)] with N > 0.",
                    nameof(value));
            }
            base.Timeout = value;
        }
    }
}
