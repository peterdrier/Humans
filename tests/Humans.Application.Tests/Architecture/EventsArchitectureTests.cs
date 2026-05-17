using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Events;
using Humans.Infrastructure.Services.Events;
using Humans.Web.Controllers;
using Humans.Web.Controllers.Api;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
    public void EventService_LivesInHumansApplicationServicesEventsNamespace()
    {
        typeof(EventService).Namespace
            .Should().Be("Humans.Application.Services.Events",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void EventService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(EventService).GetConstructors().Single();

        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "Application services must use IEventRepository instead of taking DbContext directly");
    }

    [HumansFact]
    public void EventService_TakesRepositoryInterface()
    {
        var ctor = typeof(EventService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEventRepository));
    }

    [HumansFact]
    public void IEventService_LivesInApplicationInterfacesEventsNamespace()
    {
        typeof(IEventService).Namespace
            .Should().Be("Humans.Application.Interfaces.Events");
    }

    [HumansFact]
    public void IEventRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IEventRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void EventRepository_IsSealedAndImplementsRepositoryInterface()
    {
        typeof(EventRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed; new behavior belongs on the interface");

        typeof(IEventRepository).IsAssignableFrom(typeof(EventRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void EventsRoutes_UseEventsAndBarriosSlugs()
    {
        RouteFor<EventsController>().Should().Be("Events");
        RouteFor<EventsDashboardController>().Should().Be("Events/Dashboard");
        RouteFor<EventsExportController>().Should().Be("Events/Export");
        RouteFor<EventsModerationController>().Should().Be("Events/Moderate");
        RouteFor<BarrioEventsController>().Should().Be("Barrios/{slug}/Events");
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
            RouteFor<BarrioEventsController>(),
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
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        addMethod.Invoke(null, [services]);

        var descriptor = services.Single(d => d.ServiceType == typeof(EventsFeatureFilter));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped,
            because: "MVC action filters resolve per-request; a Singleton filter would capture per-request state");
    }

    /// <summary>
    /// Single-writer rule: only <c>EventRepository</c> may invoke
    /// <c>.Add</c>, <c>.AddRange</c>, <c>.Update</c>, <c>.Remove</c>, or
    /// <c>.Attach</c> on the seven event_* DbSets. Any other production class
    /// performing such writes is a §6 cross-section boundary violation —
    /// callers must go through <see cref="IEventService"/> / <see cref="IEventRepository"/>.
    /// </summary>
    [HumansFact]
    public void Only_EventRepository_Writes_Event_DbSets()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = ScanEventDbSetWrites(repoRoot);
        RatchetTestRunner.Run(
            "OnlyEventRepositoryWritesEventDbSets",
            "tests/Humans.Application.Tests/Architecture/Baselines/OnlyEventRepositoryWritesEventDbSets.baseline.txt",
            violations);
    }

    // Write-operation chains on any of the seven event_* DbSets:
    //   ctx.Events.Add(...) / .AddRange / .Update / .Remove / .Attach
    //   ctx.EventCategories.Add(...) / ...
    //
    // Word-boundary at the start avoids matching identifiers that merely END
    // in "Events" (e.g. CalendarEvents, GoogleSyncOutboxEvents). Combined with
    // a required leading non-identifier char so we don't match property names
    // like SubmittedEvents either.
    private static readonly Regex EventWriteRegex = new(
        @"(?<![A-Za-z0-9_])(?:Events|EventCategories|EventVenues|EventFavourites|EventPreferences|EventGuideSettings|EventModerationActions)\s*\.\s*(?:Add|AddRange|Update|Remove|Attach)\b",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    internal static IEnumerable<string> ScanEventDbSetWrites(string repoRoot)
    {
        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            // The canonical owner is EventRepository — exclude it from violation reporting.
            if (path.Replace('\\', '/').EndsWith(
                    "Infrastructure/Repositories/Events/EventRepository.cs",
                    StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            if (!EventWriteRegex.IsMatch(content)) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in EventWriteRegex.Matches(content).Cast<Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                var dbset = match.Value.Split('.')[0].Trim();
                yield return $"{rel}:{dbset}-write#{ordinal} # L{line}";
            }
        }
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
    public void EventService_DoesNotInjectIMemoryCache()
    {
        // §15d — canonical Events data lives in the decorator's ConcurrentDictionary,
        // not in an IMemoryCache held by the inner service.
        var ctor = typeof(EventService).GetConstructors().Single();
        ctor.GetParameters().Should().NotContain(
            p => p.ParameterType == typeof(IMemoryCache),
            because: "inner Events service is cache-unaware; caching lives in the decorator");
    }

    [HumansFact]
    public void EventService_DoesNotInjectAnyCachingNamespaceMember()
    {
        var ctor = typeof(EventService).GetConstructors().Single();
        ctor.GetParameters().Should().NotContain(
            p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching", StringComparison.Ordinal),
            because: "design-rules §15c — Application services are cache-unaware");
    }

    [HumansFact]
    public void CachingEventService_Has_InnerServiceKey_Const()
    {
        var field = typeof(CachingEventService).GetField(
            "InnerServiceKey",
            BindingFlags.Public | BindingFlags.Static);

        field.Should().NotBeNull(
            because: "§15d — the decorator must publish the keyed DI key it uses to resolve the inner service");
        field!.GetValue(null).Should().Be("event-inner",
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
