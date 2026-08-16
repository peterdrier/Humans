using AwesomeAssertions;
using Humans.Containers.Contracts;
using Humans.Containers.Controllers;
using Humans.Containers.Data;
using Humans.Containers.Services;
using Microsoft.AspNetCore.Authorization;
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
}
