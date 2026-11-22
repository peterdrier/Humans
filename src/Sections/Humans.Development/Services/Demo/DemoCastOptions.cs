using Humans.Base.Demo;

namespace Humans.Development.Services.Demo;

/// <summary>How many humans the cast makes. Only the integration test shrinks it: purging 200 takes minutes.</summary>
internal sealed class DemoCastOptions
{
    public int Size { get; set; } = DemoCast.Size;
}
