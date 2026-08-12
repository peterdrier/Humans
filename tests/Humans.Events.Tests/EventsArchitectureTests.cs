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
        // "Public means Section or Contracts/" (design §15 step 5). Everything else in the
        // assembly is internal, including the controllers: Shell registers
        // SectionControllerFeatureProvider, which relaxes MVC's IsPublic check for assemblies
        // carrying [assembly: Section("…")], so internal controllers still route
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in
        // as many words: do not "fix" a 404 by making the controller public).
        // The section's cross-section surface is the separate Humans.Events.Contracts
        // assembly, so nothing in *this* one needs to be visible outside it.
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are
        // never hand-edited (memory/process/never-hand-edit-migrations); Store's BaselineStore
        // is public for the same reason. They are excluded rather than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Events.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
            ["Humans.Events.EventsResource", "Humans.Events.Section"],
            because: "a section exposes only its ISection entry point and its resource marker; "
                   + "the resource marker is public because the boot localization diagnostic "
                   + "discovers it via GetExportedTypes()");
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
    public void Section_Registers_DecoratorAndInvalidator_AsSameSingleton()
    {
        // §15e CRITICAL — IEventService and IEventViewInvalidator MUST resolve
        // to the same Singleton CachingEventService instance; two instances
        // would diverge and invalidations would be silently lost.
        var services = Registrations();

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

    // ── Cross-section read surface (IEventServiceRead) ───────────────────────

    [HumansFact]
    public void IEventService_DerivesFrom_IEventServiceRead()
    {
        typeof(IEventServiceRead).IsAssignableFrom(typeof(IEventService))
            .Should().BeTrue(
                because: "other sections consume the Events section through the IEventServiceRead read surface");
    }

    [HumansFact]
    public void Section_Registers_IEventServiceRead_AsSingleton()
    {
        // IEventServiceRead forwards to the same Singleton CachingEventService that
        // backs IEventService, so cross-section reads hit the existing T-03 cache.
        var descriptor = Registrations().Single(d =>
            d.ServiceType == typeof(IEventServiceRead) && d.ServiceKey is null);

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton,
            because: "the read surface forwards to the Singleton caching decorator");
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
