using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Services.Events;
using Humans.Web.Controllers;
using Humans.Web.Controllers.Api;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EventService = Humans.Application.Services.Events.EventService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/service shape for the Event
/// Guide section. The section is not public yet, so URL shape is intentionally
/// pinned here while the route rename is still fresh.
/// </summary>
public class EventsArchitectureTests
{
    [HumansFact]
    public void IEventService_LivesInApplicationInterfacesEventsNamespace()
    {
        typeof(IEventService).Namespace
            .Should().Be("Humans.Application.Interfaces.Events");
    }

    [HumansFact]
    public void EventsRoutes_UseEventsSlug()
    {
        RouteFor<EventsController>().Should().Be("Events");
        RouteFor<EventsDashboardController>().Should().Be("Events/Dashboard");
        RouteFor<EventsExportController>().Should().Be("Events/Export");
        RouteFor<EventsModerationController>().Should().Be("Events/Moderate");
        RouteFor<EventsApiController>().Should().Be("api/events");
    }

    [HumansFact]
    public void EventsRoutes_DoNotExposeOldEventGuideOrCampsSlugs()
    {
        var routeTemplates = new[]
        {
            RouteFor<EventsController>(),
            RouteFor<EventsDashboardController>(),
            RouteFor<EventsExportController>(),
            RouteFor<EventsModerationController>(),
            RouteFor<EventsApiController>()
        };

        routeTemplates.Should().NotContain(template =>
            template.Contains("EventGuide", StringComparison.OrdinalIgnoreCase)
            || template.Contains("Camps", StringComparison.OrdinalIgnoreCase)
            || template.Contains("api/guide", StringComparison.OrdinalIgnoreCase));
    }

    [HumansFact]
    public void EventsAdminController_LivesUnderEventsAdminRoute()
    {
        RouteFor<EventsAdminController>().Should().Be("Events/Admin");
    }

    [HumansFact]
    public void EventService_ImplementsIUserDataContributor()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(EventService))
            .Should().BeTrue(
                because: "EventService owns event_favourites and event_preferences (user-scoped tables); it must contribute to the GDPR Article 15 export");
    }

    [HumansFact]
    public void IEventRepository_HasNoUpdateOrDeleteForModerationActions()
    {
        var methodNames = typeof(IEventRepository)
            .GetMethods()
            .Select(m => m.Name)
            .ToArray();

        methodNames.Should().NotContain(name =>
                name.Contains("UpdateModerationAction", StringComparison.OrdinalIgnoreCase)
                || name.Contains("DeleteModerationAction", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RemoveModerationAction", StringComparison.OrdinalIgnoreCase),
            because: "event_moderation_actions is append-only; the only write entry point is SaveEventAndModerationActionAsync");
    }

    [HumansFact]
    public void EventsFeatureFilter_RegistersAsScoped()
    {
        // AddEventsSection is internal to Humans.Web — invoke it via reflection
        // to verify the filter's DI lifetime without leaking internal surface.
        var services = new ServiceCollection();
        var sectionExtensionsType = typeof(EventsController).Assembly
            .GetType("Humans.Web.Extensions.Sections.EventsSectionExtensions", throwOnError: true)!;
        var addMethod = sectionExtensionsType.GetMethod("AddEventsSection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        addMethod.Invoke(null, [services]);

        var descriptor = services.Single(d => d.ServiceType == typeof(EventsFeatureFilter));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped,
            because: "MVC action filters resolve per-request; a Singleton filter would capture per-request state");
    }

    // ── T-03: Caching decorator invariants ───────────────────────────────────

    [HumansFact]
    public void CachingEventService_ImplementsIEventService_AndIEventViewInvalidator()
    {
        typeof(IEventService).IsAssignableFrom(typeof(CachingEventService))
            .Should().BeTrue(
                because: "the decorator wraps the IEventService surface");
        typeof(IEventViewInvalidator).IsAssignableFrom(typeof(CachingEventService))
            .Should().BeTrue(
                because: "§15e — the decorator and its invalidator interface resolve to the same Singleton instance");
    }

    [HumansFact]
    public void CachingEventService_LivesInInfrastructureServicesEventsNamespace()
    {
        typeof(CachingEventService).Namespace
            .Should().Be("Humans.Infrastructure.Services.Events",
                because: "caching decorators live in Humans.Infrastructure.Services.<Section> per design-rules §15d");
    }

    [HumansFact]
    public void CachingEventService_IsSealed()
    {
        typeof(CachingEventService).IsSealed
            .Should().BeTrue(
                because: "Singleton caching decorators are sealed — extension goes on the interface");
    }

    [HumansFact]
    public void CachingEventService_Has_InnerServiceKey_Const()
    {
        var field = typeof(CachingEventService).GetField(
            "InnerServiceKey",
            BindingFlags.Public | BindingFlags.Static);

        field.Should().NotBeNull(
            because: "§15d — the decorator must publish the keyed DI key it uses to resolve the inner service");
        field.GetValue(null).Should().Be("event-inner",
            because: "convention: <section>-inner");
    }

    [HumansFact]
    public void IEventViewInvalidator_LivesInApplicationInterfacesEventsNamespace()
    {
        typeof(IEventViewInvalidator).Namespace
            .Should().Be("Humans.Application.Interfaces.Events",
                because: "the invalidator interface is the Application-layer cache-staleness signal");
    }

    [HumansFact]
    public void CachingEventService_IsItsOwnHostedService()
    {
        // Post-#587 TrackedCache self-hosting pattern: caching decorators
        // implement IHostedService directly rather than relying on an external
        // *WarmupHostedService. CachingEventService composes TrackedCache
        // (mixed-state decorator), so it owns IHostedService on the class
        // itself — same shape CachingShiftViewService uses.
        typeof(IHostedService).IsAssignableFrom(typeof(CachingEventService))
            .Should().BeTrue(
                because: "the decorator drives its own startup warmup via IHostedService");
    }

    [HumansFact]
    public void AddEventsSection_Registers_DecoratorAndInvalidator_AsSameSingleton()
    {
        // §15e CRITICAL — IEventService and IEventViewInvalidator MUST resolve
        // to the same Singleton CachingEventService instance; two instances
        // would diverge and invalidations would be silently lost.
        var services = new ServiceCollection();
        var sectionExtensionsType = typeof(EventsController).Assembly
            .GetType("Humans.Web.Extensions.Sections.EventsSectionExtensions", throwOnError: true)!;
        var addMethod = sectionExtensionsType.GetMethod("AddEventsSection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        addMethod.Invoke(null, [services]);

        var cachingDescriptor = services.Single(d =>
            d.ServiceType == typeof(CachingEventService) && d.ServiceKey is null);
        var eventServiceDescriptor = services.Single(d =>
            d.ServiceType == typeof(IEventService) && d.ServiceKey is null);
        var invalidatorDescriptor = services.Single(d =>
            d.ServiceType == typeof(IEventViewInvalidator) && d.ServiceKey is null);

        cachingDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            because: "§15d — the caching decorator is Singleton");
        eventServiceDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            because: "unkeyed IEventService maps to the Singleton decorator");
        invalidatorDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            because: "§15e — invalidator must share the decorator's singleton lifetime");
    }

    private static string RouteFor<TController>()
    {
        var route = typeof(TController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single();

        return route.Template;
    }
}
