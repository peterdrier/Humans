using AwesomeAssertions;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Events;
using Humans.Web.Controllers;
using Humans.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using EventService = Humans.Application.Services.Events.EventService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/service shape for the Event
/// Guide section. The section is not public yet, so URL shape is intentionally
/// pinned here while the route rename is still fresh.
/// </summary>
public class EventGuideArchitectureTests
{
    [HumansFact]
    public void EventGuideService_LivesInHumansApplicationServicesEventGuideNamespace()
    {
        typeof(EventService).Namespace
            .Should().Be("Humans.Application.Services.Events",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void EventGuideService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(EventService).GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "Application services must use IEventRepository instead of taking DbContext directly");
    }

    [HumansFact]
    public void EventGuideService_TakesRepositoryInterface()
    {
        var ctor = typeof(EventService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEventRepository));
    }

    [HumansFact]
    public void IEventGuideService_LivesInApplicationInterfacesEventGuideNamespace()
    {
        typeof(IEventService).Namespace
            .Should().Be("Humans.Application.Interfaces.Events");
    }

    [HumansFact]
    public void IEventGuideRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IEventRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void EventGuideRepository_IsSealedAndImplementsRepositoryInterface()
    {
        typeof(EventRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed; new behavior belongs on the interface");

        typeof(IEventRepository).IsAssignableFrom(typeof(EventRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void EventGuideRoutes_UseEventsAndBarriosSlugs()
    {
        RouteFor<EventGuideController>().Should().Be("Events");
        RouteFor<EventGuideDashboardController>().Should().Be("Events/Dashboard");
        RouteFor<EventGuideExportController>().Should().Be("Events/Export");
        RouteFor<ModerationController>().Should().Be("Events/Moderate");
        RouteFor<CampEventsController>().Should().Be("Barrios/{slug}/Events");
        RouteFor<GuideApiController>().Should().Be("api/events");
    }

    [HumansFact]
    public void EventGuideRoutes_DoNotExposeOldEventGuideOrCampsSlugs()
    {
        var routeTemplates = new[]
        {
            RouteFor<EventGuideController>(),
            RouteFor<EventGuideDashboardController>(),
            RouteFor<EventGuideExportController>(),
            RouteFor<ModerationController>(),
            RouteFor<CampEventsController>(),
            RouteFor<GuideApiController>()
        };

        routeTemplates.Should().NotContain(template =>
            template.Contains("EventGuide", StringComparison.OrdinalIgnoreCase)
            || template.Contains("Camps", StringComparison.OrdinalIgnoreCase)
            || template.Contains("api/guide", StringComparison.OrdinalIgnoreCase));
    }

    private static string RouteFor<TController>()
    {
        var route = typeof(TController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single();

        return route.Template ?? string.Empty;
    }
}
