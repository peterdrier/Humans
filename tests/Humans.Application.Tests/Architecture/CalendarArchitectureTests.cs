using AwesomeAssertions;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Caching;
using Humans.Infrastructure.Services.Calendar;
using CalendarService = Humans.Application.Services.Calendar.CalendarService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Calendar
/// section — migrated per issue #569, with the caching decorator added in
/// cache-migration plan task T-08. Pins the invariants: <c>CalendarService</c>
/// lives in Application, goes through <see cref="ICalendarRepository"/>,
/// never injects <c>DbContext</c>, and resolves owning-team display names via
/// <see cref="ITeamServiceRead"/> rather than the <c>CalendarEvent.OwningTeam</c>
/// cross-domain nav. The read surface is DTO-only and cache-backed.
/// </summary>
public class CalendarArchitectureTests
{
    // ── CalendarService ──────────────────────────────────────────────────────

    [HumansFact]
    public void CalendarService_DoesNotImportMicrosoftEntityFrameworkCore()
    {
        // The Application project reference graph structurally prevents this —
        // but pin the intent with a compile-time assertion on the assembly's
        // referenced assemblies so the test fails loudly if someone adds a
        // transitive EF reference to Humans.Application.
        var appAssembly = typeof(CalendarService).Assembly;
        appAssembly.GetReferencedAssemblies()
            .Should().NotContain(
                a => a.Name == "Microsoft.EntityFrameworkCore",
                because: "Humans.Application.csproj must not reference EF (design-rules §1)");
    }

    [HumansFact]
    public void CalendarService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Calendar §15 migration goes through ICalendarRepository, not a Store");
    }

    // ── ICalendarServiceRead / CachingCalendarService ────────────────────────

    [HumansFact]
    public void CalendarServiceRead_ReturnsNoEntityTypes()
    {
        var methods = typeof(ICalendarServiceRead).GetMethods();
        methods.Any(m => ContainsCalendarEntity(m.ReturnType)).Should().BeFalse(
            because: "ICalendarServiceRead is the DTO-only read surface; EF entities stay off the cached read contract");

        static bool ContainsCalendarEntity(Type type)
        {
            if (type == typeof(Humans.Domain.Entities.CalendarEvent) ||
                type == typeof(Humans.Domain.Entities.CalendarEventException))
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
            because: "the calendar read cache is surfaced on /Admin/CacheStats");
    }

    // ── CalendarEventInfo projection ─────────────────────────────────────────

    [HumansFact]
    public void CalendarEventInfo_IsImmutableRecord()
    {
        var t = typeof(CalendarEventInfo);
        t.IsSealed.Should().BeTrue(because: "projection records are sealed");
        // Records expose the synthesized EqualityContract property.
        t.GetMethod("get_EqualityContract", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Should().NotBeNull(because: "CalendarEventInfo must be a record");
    }

    // ── CalendarEvent ────────────────────────────────────────────────────────

    [HumansFact]
    public void CalendarEvent_OwningTeamNavIsObsolete()
    {
        var navProperty = typeof(Humans.Domain.Entities.CalendarEvent)
            .GetProperty("OwningTeam");

        navProperty.Should().NotBeNull(
            because: "EF configuration still needs the nav reference to declare FK + cascade behavior");

        navProperty.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
            .Should().NotBeEmpty(
                because: "CalendarEvent.OwningTeam is a cross-domain nav into the Teams section; resolve via ITeamService instead (design-rules §6c)");
    }

    [HumansFact]
    public void CalendarEvent_KeepsOwningTeamIdForeignKey()
    {
        typeof(Humans.Domain.Entities.CalendarEvent)
            .GetProperty("OwningTeamId")
            .Should().NotBeNull(
                because: "FK stays — only the navigation property is [Obsolete]");
    }
}
