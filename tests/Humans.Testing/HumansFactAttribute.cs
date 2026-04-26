// BannedApi: this file is the project-approved replacement for Xunit.FactAttribute.
// It is the only legitimate place to reference FactAttribute directly.
#pragma warning disable RS0030

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
                    "HumansFact requires a positive timeout in milliseconds. Infinite timeout is forbidden by project policy. Use [HumansFact(Timeout = N)] with N > 0.",
                    nameof(value));
            }
            base.Timeout = value;
        }
    }

    // Shadow inherited FactAttribute properties so tests can set them on
    // [HumansFact(...)] without RS0030 firing on the inherited (banned) member.
    public new string? Skip { get => base.Skip; set => base.Skip = value; }
    public new string? DisplayName { get => base.DisplayName; set => base.DisplayName = value; }
    public new bool Explicit { get => base.Explicit; set => base.Explicit = value; }
    public new Type? SkipType { get => base.SkipType; set => base.SkipType = value; }
    public new string? SkipUnless { get => base.SkipUnless; set => base.SkipUnless = value; }
    public new string? SkipWhen { get => base.SkipWhen; set => base.SkipWhen = value; }
    public new Type[]? SkipExceptions { get => base.SkipExceptions; set => base.SkipExceptions = value; }
}
