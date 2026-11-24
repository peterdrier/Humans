using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Auth.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Auth.Tests.Architecture;

public class AuthArchitectureTests
{
    [HumansFact]
    public void Interfaces_ResolveToSameSingleton()
    {
        // The real section registration: the same CachingSystemRoleService
        // singleton must back every interface key, or a write through one face
        // misses the cache the other face reads.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IClock>(SystemClock.Instance);

        using var provider = services.BuildServiceProvider();

        var fromManagement = provider.GetRequiredService<ISystemRoleManagement>();
        var fromRead = provider.GetRequiredService<ISystemRoleService>();
        var fromWrite = provider.GetRequiredService<ISystemRoleWrite>();
        var concrete = provider.GetRequiredService<CachingSystemRoleService>();

        ReferenceEquals(fromManagement, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
        ReferenceEquals(fromWrite, concrete).Should().BeTrue();
    }
}
