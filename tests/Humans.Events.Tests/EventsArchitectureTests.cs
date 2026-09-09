using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Gdpr.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Controllers;
using Humans.Events.Filters;
using Humans.Events.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Events.Tests;

/// <summary>
/// Architecture tests enforcing the repository/service shape and the URL shape
/// of the Events section.
/// </summary>
public class EventsArchitectureTests
{
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
    public void EventsAdminController_LivesUnderEventsAdminRoute()
    {
        RouteFor<EventsAdminController>().Should().Be("Events/Admin");
    }

    [HumansFact]
    public void EventsAdminController_RequiresEventsAdminOrAdminPolicy()
    {
        typeof(EventsAdminController).GetCustomAttribute<AuthorizeAttribute>()?.Policy
            .Should().Be("EventsAdminOrAdmin");
    }

    [HumansFact]
    public void EventsAdminSurfaces_RequireEventsAdminOrAdminPolicy()
    {
        var controllers = new[]
        {
            typeof(EventsAdminController),
            typeof(EventsDashboardController),
            typeof(EventsExportController),
            typeof(EventsModerationController),
        };

        foreach (var controller in controllers)
        {
            var authorize = controller.GetCustomAttribute<AuthorizeAttribute>();
            authorize.Should().NotBeNull(
                because: $"{controller.Name} is an Events administration surface");
            authorize!.Policy.Should().Be(PolicyNames.EventsAdminOrAdmin,
                because: $"{controller.Name} is an Events administration surface");
        }
    }

    [HumansFact]
    public void EventsSubmissionSurface_RequiresAnAuthenticatedUser()
    {
        typeof(EventsController).GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull(
                because: "event submission and personal submissions must not be anonymous");
    }

    [HumansFact]
    public void CachingEventService_ImplementsIUserDataContributor()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(CachingEventService))
            .Should().BeTrue(
                because: "the section owns event_favourites and event_preferences (user-scoped tables) and must contribute to the GDPR Article 15 export; the decorator carries it because erasure edits rows the cache serves");
    }

    [HumansFact]
    public void EventsFeatureFilter_RegistersAsScoped()
    {
        var descriptor = Registrations().Single(d => d.ServiceType == typeof(EventsFeatureFilter));

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
    public void CachingEventService_IsItsOwnHostedService()
    {
        // TrackedCache self-hosting pattern: caching decorators
        // implement IHostedService directly rather than relying on an external
        // *WarmupHostedService. CachingEventService composes TrackedCache
        // (mixed-state decorator), so it owns IHostedService on the class
        // itself — same shape CachingShiftViewService uses.
        typeof(IHostedService).IsAssignableFrom(typeof(CachingEventService))
            .Should().BeTrue(
                because: "the decorator drives its own startup warmup via IHostedService");
    }

    // ── Cross-section read surface (IEventServiceRead) ───────────────────────

    [HumansFact]
    public void IEventService_DerivesFrom_IEventServiceRead()
    {
        typeof(IEventServiceRead).IsAssignableFrom(typeof(IEventService))
            .Should().BeTrue(
                because: "other sections consume the Events section through the IEventServiceRead read surface");
    }

    /// <summary>The section's own DI registrations, from <see cref="Section.Register"/>.</summary>
    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());
        return services;
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
