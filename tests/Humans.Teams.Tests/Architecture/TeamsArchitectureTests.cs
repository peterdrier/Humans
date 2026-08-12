using System.Reflection;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
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
                         && t.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<>))
                     || (t.Namespace != null
                         && t.Namespace.StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal)),
                because: "services reach data only through their repository (design-rules §2b)");
    }

    // ── ITeamRepository + TeamRepository ─────────────────────────────────────

    [HumansFact]
    public void TeamRepository_ImplementsITeamRepository()
    {
        typeof(ITeamRepository).IsAssignableFrom(typeof(TeamRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void ITeamRepository_InjectedOnlyInsideTeamsSection()
    {
        // Scans Application + Infrastructure + Web for any non-Teams class that
        // injects ITeamRepository directly. NOT covered by the universal analyzers:
        // HUM0017 (CrossSectionRepositoryInjectionAnalyzer) is Application-only and
        // only fires on IApplicationService implementers; HUM0014 is Web-only;
        // HUM0020 only covers caching decorators. A non-decorator Infrastructure
        // class injecting ITeamRepository would otherwise go uncaught — this test
        // pins that scope.
        var assembliesToScan = new[]
        {
            typeof(TeamService).Assembly,                                // Humans.Teams
            typeof(HumansMetricsService).Assembly,                       // Humans.Infrastructure
            typeof(UserService).Assembly,                                // Humans.Application
        };

        var violations = new List<string>();
        foreach (var assembly in assembliesToScan)
        {
            foreach (var type in assembly.GetTypes()
                         .Where(t => t.IsClass && !t.IsAbstract))
            {
                if (IsTeamsSectionType(type))
                    continue;

                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (var parameter in ctor.GetParameters())
                    {
                        if (parameter.ParameterType == typeof(ITeamRepository))
                        {
                            violations.Add($"{type.FullName}:{parameter.ParameterType.Name}");
                        }
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            because: "non-Teams sections must read teams via ITeamService (cache-backed), not ITeamRepository (DB-direct). " +
                     "T-02 (docs/plans/2026-05-16-cache-migration.md) removes the last bypass; this test pins the rule.");
    }

    private static bool IsTeamsSectionType(Type type)
    {
        var ns = type.Namespace;
        if (ns is null)
            return false;

        // Teams section homes for production code that legitimately injects ITeamRepository:
        //   - Humans.Teams.Services.*   (TeamService and helpers)
        //   - Humans.Teams.Data.* (the EF impl itself)
        return ns.StartsWith("Humans.Teams.Services", StringComparison.Ordinal)
            || ns.StartsWith("Humans.Teams.Data", StringComparison.Ordinal);
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
            "Humans.Teams.Section",
            "Humans.Teams.TeamsResource",
        ]);
    }

}
