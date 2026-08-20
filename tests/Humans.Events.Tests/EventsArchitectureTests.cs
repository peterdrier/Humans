using System.Reflection;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Events.Contracts;
using Humans.Events.Controllers;
using Humans.Events.Data;
using Humans.Events.Filters;
using Humans.Events.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Events.Tests;

/// <summary>
/// Architecture tests enforcing the repository/service shape for the Event
/// Guide section. The section is not public yet, so URL shape is intentionally
/// pinned here while the route rename is still fresh.
/// </summary>
/// <remarks>
/// The three namespace-pinning tests this file used to carry (IEventService,
/// CachingEventService and IEventViewInvalidator each asserting a
/// <c>Humans.Application.*</c> / <c>Humans.Infrastructure.*</c> namespace) are gone:
/// the assembly boundary subsumes them (design §15 step 11). Everything asserted
/// here now lives in <c>Humans.Events</c> by construction — this test project can
/// only see it through <c>InternalsVisibleTo</c>.
/// </remarks>
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
    public void EventsAdminController_RequiresEventsAdminOrAdminPolicy()
    {
        // Moved from Humans.Application.Tests' EndpointAuthorizationTests, which sweeps
        // Shell's controllers and can no longer name this one by type.
        typeof(EventsAdminController).GetCustomAttribute<AuthorizeAttribute>()?.Policy
            .Should().Be("EventsAdminOrAdmin");
    }

    [HumansFact]
    public void OnlySectionAndResourceArePublic()
    {
        // Public means Section, Contracts/, the resource marker, or a type the framework
        // silently drops when internal. Controllers stay internal —
        // SectionControllerFeatureProvider routes them; do not "fix" a 404 by going public.
        // The two view components: Razor only builds a <vc:> tag from a public class.
        // SectionAdminNav stays internal too — Shell finds it via GetTypes(), not
        // GetExportedTypes() (nobodies-collective/Humans#1077).
        // Migrations are emitted public by dotnet ef, so they are excluded below.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Events.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
            [
                "Humans.Events.Contracts.FavouriteButtonModel",
                "Humans.Events.EventsResource",
                "Humans.Events.Section",
                "Humans.Events.ViewComponents.EventsCardViewComponent",
                "Humans.Events.ViewComponents.EventsSearchResultViewComponent",
            ],
            because: "a section exposes its entry point, resource marker, Contracts/ folder, "
                   + "and what the framework needs public — nothing else");
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().NotBeEmpty();
        controllers.Should().OnlyContain(t => !t.IsPublic,
            because: "SectionControllerFeatureProvider discovers internal controllers in section "
                   + "assemblies; a public one would be nameable from any other section");
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
        // Post-#587 TrackedCache self-hosting pattern: caching decorators
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

    /// <summary>
    /// The section's own DI registrations. Since G5 these come from
    /// <see cref="Section.Register"/> rather than a Shell extension method, so the
    /// reflection that used to reach into <c>EventsSectionExtensions</c> is gone.
    /// </summary>
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
