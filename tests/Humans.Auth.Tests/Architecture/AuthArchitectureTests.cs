using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Auth.Services;
using Humans.Base;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Auth.Tests.Architecture;

/// <summary>
/// Architecture tests for the Auth section's role-assignment faces.
/// </summary>
public class AuthArchitectureTests
{
    [HumansFact]
    public void IRoleAssignmentManagement_IRoleAssignmentService_And_IRoleAssignmentWrite_ResolveToSameSingleton()
    {
        // The real section registration: the same CachingRoleAssignmentService
        // singleton must back every interface key, or a write through one face
        // misses the cache the other face reads.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IClock>(SystemClock.Instance);

        using var provider = services.BuildServiceProvider();

        var fromManagement = provider.GetRequiredService<IRoleAssignmentManagement>();
        var fromRead = provider.GetRequiredService<IRoleAssignmentService>();
        var fromWrite = provider.GetRequiredService<IRoleAssignmentWrite>();
        var concrete = provider.GetRequiredService<CachingRoleAssignmentService>();

        ReferenceEquals(fromManagement, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
        ReferenceEquals(fromWrite, concrete).Should().BeTrue();
    }
}
