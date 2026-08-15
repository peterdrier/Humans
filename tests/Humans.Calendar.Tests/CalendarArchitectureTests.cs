using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Calendar.Data;
using Humans.Calendar.Domain;
using Humans.Calendar.Services;
using Humans.Calendar.Services.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Calendar.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Calendar
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/CalendarArchitectureTests.cs</c>. Its
/// <c>CalendarService_DoesNotImportMicrosoftEntityFrameworkCore</c> test is gone: it asserted
/// that <c>Humans.Application</c> carries no EF reference, and the section assembly holds the
/// repository and legitimately does. The invariant it was reaching for — the service never
/// touches a <c>DbContext</c> — is asserted directly on the constructor instead, which is
/// stronger and survives the move. The §15 decorator, DTO-only read surface and no-cross-section-nav
/// assertions carry over unchanged.
/// </remarks>
public class CalendarArchitectureTests
{
    [HumansFact]
    public void OnlySectionAndResourceArePublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5). CalendarResource is the one
        // sanctioned extra: the boot localization diagnostic discovers section resource markers
        // through GetExportedTypes(), so an internal marker is skipped in silence (§15 step 3b).
        //
        // CalendarController is internal. Shell registers SectionControllerFeatureProvider, which
        // relaxes MVC's IsPublic check for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are never
        // hand-edited (memory/process/never-hand-edit-migrations); they are excluded rather
        // than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Calendar.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
        [
            // The personal iCal feed's cross-section surface (G5 lane 4b-2c): Shifts and
            // Events implement the contributor, Scanner and Shell consume the orchestrator,
            // and <vc:user-calendar> needs a public component or it ships as inert markup.
            "Humans.Calendar.CalendarResource",
            "Humans.Calendar.Contracts.CalendarFeedItem",
            "Humans.Calendar.Contracts.ICalendarFeedContributor",
            "Humans.Calendar.Contracts.IICalFeedService",
            "Humans.Calendar.Contracts.UserCalendarViewComponent",
            "Humans.Calendar.Contracts.UserCalendarViewModel",
            "Humans.Calendar.Section",
        ]);
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        // CalendarController (/Calendar) and ICalFeedApiController (/api/ical).
        controllers.Should().HaveCount(2);
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

    [HumansFact]
    public void ICalFeedApiControllerKeepsItsRoutePrefix()
    {
        // /api/ical/{userId}/{token}.ics is a URL calendar clients have already
        // subscribed to — a G5 move changes files, never routes.
        var type = typeof(Section).Assembly
            .GetType("Humans.Calendar.Controllers.ICalFeedApiController", throwOnError: true)!;

        type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single().Template
            .Should().Be("api/ical");
    }

    [HumansFact]
    public void SectionRegistersTheICalFeedOrchestrator()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        var descriptor = services.Single(d => d.ServiceType == typeof(Contracts.IICalFeedService));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped,
            because: "contributors share the scoped section DbContexts the fan-out reads through");
        descriptor.ImplementationType.Should().Be(typeof(ICalFeedService));
    }

    [HumansFact]
    public void ControllerKeepsItsRoutePrefix()
    {
        // Every /Calendar URL is unchanged — a G5 move changes files, never routes.
        var type = typeof(Section).Assembly
            .GetType("Humans.Calendar.Controllers.CalendarController", throwOnError: true)!;

        type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single().Template
            .Should().Be("Calendar");
    }

    [HumansFact]
    public void AuditDiscriminatorsAreLiteralsNotDerivedFromTypeNames()
    {
        // The audit_log rows already in the database carry these strings and are matched by
        // exact equality on read. Pinning them is what makes a future rename over this section
        // schema-inert (memory/code/type-name-as-persisted-string.md). "Team" is the extra case:
        // it named an entity in a section that has not moved yet, so nameof(Team) would have
        // stopped compiling at Teams' own G5.
        AuditEntityTypes.CalendarEvent.Should().Be("CalendarEvent");
        AuditEntityTypes.Team.Should().Be("Team");
    }

    [HumansFact]
    public void CalendarService_ConstructorTakesNoEfType()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        parameterTypes.Should().NotContain(t => typeof(DbContext).IsAssignableFrom(t),
            because: "the service goes through ICalendarRepository; only the repository owns a DbContext");
        parameterTypes.Should().NotContain(
            t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDbContextFactory<>),
            because: "context lifetime is the repository's business (design-rules §3)");
    }

    /// <summary>
    /// Pins the set of types that may inject <see cref="ICalendarRepository"/>: the owning
    /// service and the repository implementation. A new consumer taking the repository directly
    /// would bypass the service layer and the single-writer rule for the <c>calendar_*</c> tables.
    /// </summary>
    [HumansFact]
    public void ICalendarRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Calendar.Services.CalendarService",
            "Humans.Calendar.Data.CalendarRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICalendarRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the calendar_* tables must go through CalendarService");
    }

    [HumansFact]
    public void CalendarServiceRead_ReturnsNoEntityTypes()
    {
        var methods = typeof(ICalendarServiceRead).GetMethods();
        methods.Any(m => ContainsCalendarEntity(m.ReturnType)).Should().BeFalse(
            because: "ICalendarServiceRead is the DTO-only read surface; EF entities stay off the cached read contract");

        static bool ContainsCalendarEntity(Type type)
        {
            if (type == typeof(CalendarEvent) || type == typeof(CalendarEventException))
            {
                return true;
            }

            return type.IsGenericType && type.GetGenericArguments().Any(ContainsCalendarEntity);
        }
    }

    [HumansFact]
    public void CachingCalendarService_ImplementsReadAndWriteSurfaces()
    {
        typeof(CachingCalendarService).Should().BeAssignableTo<ICalendarServiceRead>(
            because: "unkeyed ICalendarServiceRead resolves to the cache-backed read service");
        typeof(CachingCalendarService).Should().BeAssignableTo<ICalendarService>(
            because: "write calls still pass through the decorator so the read cache refreshes after mutations");
    }

    [HumansFact]
    public void CachingCalendarService_IsTrackedCache()
    {
        typeof(CachingCalendarService).Should().BeAssignableTo<ICacheStats>(
            because: "the calendar read cache is surfaced on /Debug/CacheStats");
    }

    [HumansFact]
    public void CalendarEventInfo_IsImmutableRecord()
    {
        var t = typeof(CalendarEventInfo);
        t.IsSealed.Should().BeTrue(because: "projection records are sealed");
        // Records expose the synthesized EqualityContract property.
        t.GetMethod("get_EqualityContract", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Should().NotBeNull(because: "CalendarEventInfo must be a record");
    }

    [HumansFact]
    public void CalendarEvent_HasNoOwningTeamNav()
    {
        typeof(CalendarEvent)
            .GetProperty("OwningTeam")
            .Should().BeNull(
                because: "CalendarEvent.OwningTeam was a cross-domain nav into the Teams section; the FK is now " +
                          "a bare column (design-rules §6c, memory/architecture/no-cross-section-ef-joins.md)");
    }

    [HumansFact]
    public void CalendarEvent_KeepsOwningTeamIdForeignKey()
    {
        typeof(CalendarEvent)
            .GetProperty("OwningTeamId")
            .Should().NotBeNull(
                because: "FK stays — only the navigation property is gone");
    }

    [HumansFact]
    public void SectionRegistersTheKeyedInnerServiceAndTheSingletonDecorator()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(ICalendarRepository)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);

        // The undecorated service is keyed; unkeyed ICalendarService/ICalendarServiceRead must
        // both resolve to the one CachingCalendarService singleton, or a write would refresh a
        // different cache than the read serves (§15e).
        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(ICalendarService)
            && d.IsKeyedService
            && Equals(d.ServiceKey, CachingCalendarService.InnerServiceKey));

        services.Single(d => d.ServiceType == typeof(ICalendarService) && !d.IsKeyedService).Lifetime
            .Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(ICalendarServiceRead)).Lifetime
            .Should().Be(ServiceLifetime.Singleton);
        services.Should().ContainSingle(d => d.ServiceType == typeof(ICacheStats));
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The carve moved every Calendar_* key out of SharedResource, so a type still injecting
        // IStringLocalizer<SharedResource> would resolve nothing and render the raw key — a 200
        // with degraded copy, in every language, on paths a render test tends not to reach.
        // The views are safe by construction (_ViewImports rebinds Localizer for all of them);
        // this is the guard for controllers and services.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && p.ParameterType.GetGenericArguments()[0] != typeof(CalendarResource))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every Calendar_* key lives in CalendarResource; resolving one through another "
                   + "set renders the key itself and no error (§15 step 3b)");
    }
}
