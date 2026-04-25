using System.Runtime.CompilerServices;
using Xunit;

namespace Humans.Testing;

public sealed class HumansFactAttribute : FactAttribute
{
    public HumansFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        base.Timeout = 1000;
    }

    public new int Timeout
    {
        get => base.Timeout;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentException(
                    "HumansFact requires a positive timeout in milliseconds. Infinite timeout is forbidden by project policy. Use [HumansFact(Timeout = N)] with N > 0.",
                    nameof(value));
            }
            base.Timeout = value;
        }
    }
}
