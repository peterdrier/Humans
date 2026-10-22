using Humans.Base.Interfaces;
using Humans.CityPlanning.Services;
using Microsoft.AspNetCore.Builder;

namespace Humans.CityPlanning;

/// <summary>The hub stays <c>internal</c> because this call names it from inside the assembly.</summary>
internal sealed class SectionEndpoints : ISectionEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<CityPlanningHub>("/hubs/city-planning");
    }
}
