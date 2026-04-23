using AwesomeAssertions;
using Humans.Application.Interfaces.Calendar;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Repositories.Calendar;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CalendarService = Humans.Application.Services.Calendar.CalendarService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Calendar
/// section — migrated per issue #569. Pins the invariants:
/// <c>CalendarService</c> lives in Application, goes through
/// <see cref="ICalendarRepository"/>, never injects <c>DbContext</c>, and
/// resolves owning-team display names via <see cref="ITeamService"/> rather
/// than the <c>CalendarEvent.OwningTeam</c> cross-domain nav. Calendar did
/// not get a caching decorator — short-TTL <c>IMemoryCache</c> stays
/// in-service per design-rules §15f.
/// </summary>
public class CalendarArchitectureTests
{
    // ── CalendarService ──────────────────────────────────────────────────────

    [Fact]
    public void CalendarService_LivesInHumansApplicationServicesCalendarNamespace()
    {
        typeof(CalendarService).Namespace
            .Should().Be("Humans.Application.Services.Calendar",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void CalendarService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ICalendarRepository instead (design-rules §3)");
    }

    [Fact]
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

    [Fact]
    public void CalendarService_TakesRepository()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICalendarRepository));
    }

    [Fact]
    public void CalendarService_TakesTeamService()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamService),
            because: "owning-team display names are resolved via ITeamService cross-section (design-rules §6b, §9); CalendarEvent.OwningTeam nav is [Obsolete]");
    }

    [Fact]
    public void CalendarService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(CalendarService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Calendar §15 migration does not use a store — short-TTL IMemoryCache in-service is sufficient (§15f)");
    }

    // ── ICalendarRepository ──────────────────────────────────────────────────

    [Fact]
    public void ICalendarRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ICalendarRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void CalendarRepository_IsSealed()
    {
        var repoType = typeof(CalendarRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    // ── CalendarEvent ────────────────────────────────────────────────────────

    [Fact]
    public void CalendarEvent_OwningTeamNavIsObsolete()
    {
        var navProperty = typeof(Humans.Domain.Entities.CalendarEvent)
            .GetProperty("OwningTeam");

        navProperty.Should().NotBeNull(
            because: "EF configuration still needs the nav reference to declare FK + cascade behavior");

        navProperty!.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
            .Should().NotBeEmpty(
                because: "CalendarEvent.OwningTeam is a cross-domain nav into the Teams section; resolve via ITeamService instead (design-rules §6c)");
    }

    [Fact]
    public void CalendarEvent_KeepsOwningTeamIdForeignKey()
    {
        typeof(Humans.Domain.Entities.CalendarEvent)
            .GetProperty("OwningTeamId")
            .Should().NotBeNull(
                because: "FK stays — only the navigation property is [Obsolete]");
    }
}
