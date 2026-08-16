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
    public void AuditEntityTypes_AreLiterals_NotNameof()
    {
        // These are literal string values we store in the DB. Pinned so a rename can't
        // quietly change them and orphan existing audit_log rows
        // (memory/code/type-name-as-persisted-string.md).
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
