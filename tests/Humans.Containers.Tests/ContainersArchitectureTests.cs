using AwesomeAssertions;
using Humans.Containers.Contracts;
using Humans.Containers.Controllers;
using Humans.Containers.Data;
using Humans.Containers.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Containers.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Containers
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class ContainersArchitectureTests
{
    [HumansFact]
    public void OnlySectionResourceAndContractsArePublic()
    {
        // "Public means Section, <Section>Resource or Contracts/" (design §15 steps 5, 5b).
        // Containers is the first section whose Contracts/ is a *folder* rather than a
        // separate project: every consumer outside the section (CityPlanningController,
        // CityPlanningApiController) lives in Shell, and Shell references the section, so
        // nothing in Base needs to see this surface and no downward carve is required.
        //
        // Everything else is internal, including the controller: Shell registers
        // SectionControllerFeatureProvider, which relaxes MVC's IsPublic check for assemblies
        // carrying [assembly: Section("…")], so internal controllers still route
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in
        // as many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are
        // never hand-edited (memory/process/never-hand-edit-migrations); they are excluded
        // rather than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Containers.Data.Migrations", StringComparison.Ordinal))
            .Where(t => !string.Equals(t.Namespace, "Humans.Containers.Contracts", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
            ["Humans.Containers.ContainersResource", "Humans.Containers.Section"],
            because: "outside Contracts/ a section exposes only its ISection entry point and its "
                   + "resource marker; the resource marker is public because the boot localization "
                   + "diagnostic discovers it via GetExportedTypes()");
    }

    [HumansFact]
    public void EveryPublicTypeOutsideContractsIsAccountedFor()
    {
        // The companion to the test above: nothing may become public by drifting *into*
        // Contracts/ either. Contracts/ is a namespace, and this pins the whole of it,
        // so widening the cross-section surface is a visible diff rather than a silent one.
        var contractTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => string.Equals(t.Namespace, "Humans.Containers.Contracts", StringComparison.Ordinal))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        contractTypes.Should().BeEquivalentTo(
        [
            "ContainerAdminOverview",
            "ContainerAuthorizationTarget",
            "ContainerCampGroup",
            "ContainerCardModel",
            "ContainerData",
            "ContainerDto",
            "ContainerFormModel",
            "ContainerImageUpload",
            "ContainerIndexViewModel",
            "ContainerOperation",
            "ContainerOperationRequirement",
            "ContainerPlacementDto",
            "ContainerPlacementViewModel",
            "ContainerViewModel",
            "ContainerWithPlacement",
            "ContainerWithPlacementViewModel",
            "IContainerService",
        ],
            because: "Contracts/ is the section's whole cross-section surface (design §15 step 5b); "
                   + "adding to it is a decision, not an accident");
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
    public void ContainerRoutes_HangOffTheCampSlug()
    {
        RouteFor<ContainerController>().Should().Be("Camp/{slug}/Containers");
    }

    [HumansFact]
    public void ContainerController_RequiresAuthorization()
    {
        typeof(ContainerController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Should().ContainSingle(
                because: "every container action is camp-scoped and authorized per resource");
    }

    [HumansFact]
    public void Section_RegistersRepositoryAsSingletonAndServiceAsScoped()
    {
        var services = Registrations();

        services.Single(d => d.ServiceType == typeof(IContainerRepository)).Lifetime
            .Should().Be(ServiceLifetime.Singleton,
                because: "the repository owns its DbContext lifetime via IDbContextFactory");
        services.Single(d => d.ServiceType == typeof(IContainerService)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
    }

    [HumansFact]
    public void Section_RegistersItsOwnAuthorizationHandler()
    {
        // §15 step 6: resource-based handlers move into the section; the *policies* stay in
        // Shell's AuthorizationPolicyExtensions.
        Registrations().Should().ContainSingle(d =>
            d.ServiceType == typeof(IAuthorizationHandler)
            && d.ImplementationType!.Name == "ContainerAuthorizationHandler");
    }

    [HumansFact]
    public void AuditEntityTypes_AreLiterals_NotNameof()
    {
        // Persisted audit discriminators are a data contract with rows already in the
        // database. Container and ContainerPlacement kept their CLR names through the move;
        // Camp is not even nameable from this assembly. All three are literals so a future
        // rename stays schema-inert (memory/code/type-name-as-persisted-string.md).
        AuditEntityTypes.Container.Should().Be("Container");
        AuditEntityTypes.ContainerPlacement.Should().Be("ContainerPlacement");
        AuditEntityTypes.Camp.Should().Be("Camp");
    }

    /// <summary>
    /// The section's own DI registrations. Since G5 these come from
    /// <see cref="Section.Register"/> rather than a Shell extension method.
    /// </summary>
    private static ServiceCollection Registrations()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());
        return services;
    }

    private static string RouteFor<TController>() =>
        typeof(TController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Single()
            .Template;
}
