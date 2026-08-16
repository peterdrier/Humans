using System.Reflection;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces;
using Humans.Application.Services.Dashboard;
using Humans.Infrastructure.Services;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TeamService = Humans.Teams.Services.TeamService;

namespace Humans.Teams.Tests;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Teams
/// section — migrated per issue #540 (§15 Part 1 — TeamService core).
/// Pins the invariants:
/// <list type="bullet">
/// <item><description><c>TeamService</c> lives in <c>Humans.Teams.Services</c>.</description></item>
/// <item><description><c>TeamService</c> never injects <c>DbContext</c> — all data access flows through <see cref="ITeamRepository"/>.</description></item>
/// <item><description><c>TeamService</c> never imports <c>Microsoft.EntityFrameworkCore</c> (structurally enforced by the project reference graph — this test acts as a defence-in-depth).</description></item>
/// <item><description><see cref="ITeamRepository"/> lives in <c>Humans.Teams.Data</c> and has a sealed EF-backed implementation.</description></item>
/// </list>
/// Teams uses the §15 caching decorator pattern: <see cref="CachingTeamService"/>
/// wraps the keyed inner <see cref="ITeamService"/> and exposes the read split
/// via <see cref="ITeamServiceRead"/>.
/// </summary>
public class TeamsArchitectureTests
{
    // ── TeamService ──────────────────────────────────────────────────────────

    [HumansFact]
    public void TeamService_TakesNoDataAccessDependency()
    {
        // Restated at G5 (Calendar finding 41): the section assembly holds the repository and
        // legitimately references EF Core, so the old "this assembly has no EF reference"
        // assertion is false here rather than vacuous. The invariant that actually matters is
        // the constructor's — the service reaches data only through ITeamRepository.
        var ctor = typeof(TeamService).GetConstructors().Single();

        ctor.GetParameters()
            .Select(p => p.ParameterType)
            .Should().NotContain(
                t => typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(t)
                     || (t.IsGenericType
                         && t.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<>)),
                because: "services reach data only through their repository (design-rules §2b)");
    }

    // ── ITeamRepository + TeamRepository ─────────────────────────────────────

    [HumansFact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

    // ── ITeamServiceRead split (memory/architecture/section-read-write-split.md) ──

    [HumansFact]
    public void ITeamService_InheritsITeamServiceRead()
    {
        typeof(ITeamServiceRead).IsAssignableFrom(typeof(ITeamService))
            .Should().BeTrue(
                because: "ITeamService is the full Teams surface; external sections inject the narrow ITeamServiceRead. " +
                         "See memory/architecture/section-read-write-split.md.");
    }

    [HumansFact]
    public void CachingTeamService_ImplementsITeamServiceRead()
    {
        typeof(ITeamServiceRead).IsAssignableFrom(typeof(CachingTeamService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void ITeamService_And_ITeamServiceRead_ResolveToSameSingleton()
    {
        // Mirrors the Teams-section DI shape: the same CachingTeamService
        // singleton is exposed under both interface keys.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITeamRepository>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<ILogger<CachingTeamService>>());

        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamServiceRead>(sp => sp.GetRequiredService<CachingTeamService>());

        using var provider = services.BuildServiceProvider();

        var fromFull = provider.GetRequiredService<ITeamService>();
        var fromRead = provider.GetRequiredService<ITeamServiceRead>();
        var concrete = provider.GetRequiredService<CachingTeamService>();

        ReferenceEquals(fromFull, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
    }

    // ── Section boundary (design §15 steps 3b and 5) ─────────────────────────

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSetOrSharedResource()
    {
        // A view is safe by construction — _ViewImports rebinds Localizer in one line. A
        // controller that kept IStringLocalizer<SharedResource> compiles and renders its
        // carved keys as raw key names on the failure branches a render test never reaches
        // (Surveys' finding, in Governance's two-marker form: five Teams keys stayed in
        // SharedResource because something outside the section renders them too).
        var allowed = new[] { typeof(TeamsResource), typeof(Humans.UI.SharedResource) };

        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .SelectMany(m => m.GetParameters()))
                .Select(p => (Type: t, p.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && x.ParameterType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Localization.IStringLocalizer<>))
            .Where(x => !allowed.Contains(x.ParameterType.GetGenericArguments()[0]))
            .Select(x => $"{x.Type.FullName}:{x.ParameterType.GetGenericArguments()[0].Name}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "the section localizes through TeamsResource; SharedResource is allowed only for the five "
                     + "co-owned keys and the Enum_SlotPriority_* / Enum_RolePeriod_* reads (design §15 step 3b)");
    }

    [HumansFact]
    public void TheSectionExportsOnlyItsSectionMarkerResourceMarkerAndContractsFolder()
    {
        // What HUM0034 enforces mechanically, stated in the section's own terms.
        var exported = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !t.Namespace!.StartsWith("Humans.Teams.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        exported.Should().BeEquivalentTo(
        [
            "Humans.Teams.Contracts.HumansTeamControllerBase",
            // Shell's widget gallery binds it (WidgetGalleryController.SampleShiftsSummary),
            // so it cannot be internal. Under Contracts/ rather than Models/ for that reason
            // — G5 lane 4b-i, nobodies-collective/Humans#866. It is Teams' and not Shifts'
            // because Teams both builds and renders it, and Humans.Shifts already references
            // Humans.Teams, so the plan's Shifts destination would have been a cycle.
            "Humans.Teams.Contracts.ShiftsSummaryCardViewModel",
            "Humans.Teams.Section",
            "Humans.Teams.TeamsResource",
        ]);
    }

}
