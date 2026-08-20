using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tour;

/// <summary>
/// Tour's DI entry point, at the project root by convention. Discovered by Shell.
/// Register is empty: Tour owns no tables and no services — it is one anonymous
/// controller rendering static content. The type exists because ISection is what puts
/// the assembly in SectionDiscoveryExtensions' discovered-sections log (the Scanner shape).
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally empty — see the remarks above.
    }
}
