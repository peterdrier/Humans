using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tour;

/// <summary>
/// Tour's DI entry point. Register is empty by design (no tables, no services); the
/// ISection type is what makes the assembly a discovered section — delete it and the
/// internal controller is no longer routed, so /Tour 404s with a green build.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
