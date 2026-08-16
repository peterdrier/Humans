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
