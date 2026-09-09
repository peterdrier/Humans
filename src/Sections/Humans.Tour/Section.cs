using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tour;

/// <summary>
/// Tour's DI entry point. Register is empty — the section owns no tables and no services —
/// but the type still matters: implementing ISection is what makes the assembly a section
/// for discovery and routing.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
    }
}
