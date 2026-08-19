using Microsoft.AspNetCore.Routing;

namespace Humans.Base.Interfaces;

/// <summary>
/// The endpoints a section maps itself — SignalR hubs and anything else needing
/// <see cref="IEndpointRouteBuilder"/>. Controllers are already discovered by the MVC
/// feature providers; this seam is for what routing cannot find on its own.
/// </summary>
/// <remarks>
/// Runs at endpoint-mapping time, not <see cref="ISection.Register"/> — the route builder
/// exists only after the app is built.
/// </remarks>
public interface ISectionEndpoints : ISectionContribution
{
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
