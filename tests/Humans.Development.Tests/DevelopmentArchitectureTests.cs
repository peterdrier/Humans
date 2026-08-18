using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Development.Tests;

/// <summary>
/// Safety check for the Development section.
/// </summary>
public class DevelopmentArchitectureTests
{
    [HumansFact]
    public void Register_binds_nothing_in_production_or_when_the_environment_is_unknown()
    {
        // The Development section registers seeders that create real login accounts.
        // Register() is supposed to skip them unless we're in a dev environment.
        //
        // This checks it skips them for "Production", for a different capitalisation of it,
        // and for a missing or blank environment name — i.e. when in doubt, register nothing.
        //
        // Nothing else can check this: the test host always runs as "Testing", so the
        // integration tests only ever exercise the case where the seeders DO get registered.
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
