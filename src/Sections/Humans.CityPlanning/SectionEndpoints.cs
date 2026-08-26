using Humans.Base.Interfaces;
using Humans.CityPlanning.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Humans.CityPlanning;

/// <summary>
/// City Planning's endpoint contribution, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix. Maps the live-cursor hub, which
/// stays <c>internal</c> because this call names the type from inside the assembly.
/// </summary>
internal sealed class SectionEndpoints : ISectionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<CityPlanningHub>("/hubs/city-planning");
    }
}
