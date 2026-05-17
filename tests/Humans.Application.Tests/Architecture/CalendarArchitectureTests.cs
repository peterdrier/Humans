using AwesomeAssertions;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Repositories.Calendar;
using Humans.Infrastructure.Services.Calendar;
using CalendarService = Humans.Application.Services.Calendar.CalendarService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Calendar
/// section — migrated per issue #569, with the caching decorator added in
/// cache-migration plan task T-08. Pins the invariants: <c>CalendarService</c>
/// lives in Application, goes through <see cref="ICalendarRepository"/>,
/// never injects <c>DbContext</c>, and resolves owning-team display names via
/// <see cref="ITeamService"/> rather than the <c>CalendarEvent.OwningTeam</c>
/// cross-domain nav. <see cref="CachingCalendarService"/> wraps the inner
/// service as a Singleton decorator and is the only type that holds the
/// <see cref="CalendarEventInfo"/> read-model.
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
    public void CalendarService_TakesRepository()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICalendarRepository));
    }

    [HumansFact]
    public void CalendarService_TakesTeamService()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamService),
            because: "owning-team display names are resolved via ITeamService cross-section (design-rules §6b, §9); CalendarEvent.OwningTeam nav is [Obsolete]");
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

    [HumansFact]
    public void CalendarService_DoesNotInjectIMemoryCache()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType.FullName ?? string.Empty).ToList();

        paramTypes.Should().NotContain(
            n => n.Contains("Microsoft.Extensions.Caching.Memory.IMemoryCache", StringComparison.Ordinal),
            because: "T-08: the canonical CalendarEventInfo cache lives on CachingCalendarService in Infrastructure; the inner service is cache-free.");
    }

    // ── CachingCalendarService ───────────────────────────────────────────────

    [HumansFact]
    public void CachingCalendarService_ImplementsICalendarService()
    {
        typeof(CachingCalendarService).Should().BeAssignableTo<ICalendarService>(
            because: "decorator pattern — Singleton wraps the keyed Scoped inner ICalendarService");
    }

    [HumansFact]
    public void CachingCalendarService_IsSealed()
    {
        typeof(CachingCalendarService).IsSealed.Should().BeTrue(
            because: "decorator implementations are sealed to prevent ad-hoc extension");
    }

    [HumansFact]
    public void CachingCalendarService_IsTrackedCache()
    {
        // TrackedCache<Guid, CalendarEventInfo> base — surfaces hit/miss/invalidation
        // counters on /Admin/CacheStats via ICacheStats.
        typeof(CachingCalendarService).Should().BeAssignableTo<ICacheStats>(
            because: "T-08 surfaces the CalendarEventInfo cache on /Admin/CacheStats");
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

    // ── ICalendarRepository ──────────────────────────────────────────────────

    [HumansFact]
    public void CalendarRepository_IsSealed()
    {
        var repoType = typeof(CalendarRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
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
