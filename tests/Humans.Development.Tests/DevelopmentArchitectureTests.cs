using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Development.Tests;

public class DevelopmentArchitectureTests
{
    [HumansFact]
    public void Register_binds_nothing_in_production_or_when_the_environment_is_unknown()
    {
        // Register() must skip the seeders, which create real login accounts, outside a dev
        // environment — when in doubt, register nothing. Only this can check it: the test host
        // always runs as "Testing", where the seeders DO get registered.
        Register(Environments.Production).Should().BeEmpty();
        Register("PRODUCTION").Should().BeEmpty();
        Register(environmentName: null).Should().BeEmpty();
        Register(environmentName: "   ").Should().BeEmpty();
    }

    private static IServiceCollection Register(string? environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [HostDefaults.EnvironmentKey] = environmentName,
            })
            .Build();

        var services = new ServiceCollection();
        new Section().Register(services, configuration);
        return services;
    }
}
