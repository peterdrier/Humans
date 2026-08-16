using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;

namespace Humans.Development.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Development
/// (nobodies-collective/Humans#866, G5).
/// </summary>
public class DevelopmentArchitectureTests
{


    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // Gate's structural guard in its strict form, not Debug's one-marker variant: nothing
        // here localizes at all. The section has no Resources/ folder, no Development_* key in
        // SharedResource and no Enum_Development* key, so the day someone adds page copy the
        // build tells them to carve a resource set rather than letting it resolve against the
        // ambient shared set — which is what ConsentController did, silently, past a green
        // suite (§15 step 3b). Method parameters are swept as well as constructor ones,
        // because Debug's offender was a [FromServices] argument on an action.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                .Where(p => p.ParameterType.IsGenericType
                            && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
                .Select(_ => t.FullName ?? t.Name))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Development ships no resource set; a type that needs localized copy must "
                   + "carve one first");
    }

    [HumansFact]
    public void SectionTakesNoDbContextOrRepository()
    {
        // Calendar's rule (§15 step 11): the assembly-level "does not reference
        // Microsoft.EntityFrameworkCore" assertion is restated on the constructors, which is
        // what it was reaching for. Development owns no tables and takes no
        // Humans.Infrastructure reference; every write goes through another section's service.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => p.ParameterType.Name.EndsWith("DbContext", StringComparison.Ordinal)
                            || p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal)
                            || (p.ParameterType.IsGenericType
                                && string.Equals(p.ParameterType.GetGenericTypeDefinition().Name, "IDbContextFactory`1",
                                    StringComparison.Ordinal))
                            || string.Equals(p.ParameterType.Namespace, "Humans.Application.Interfaces.Stores",
                                StringComparison.Ordinal))
                .Select(p => $"{t.FullName}({p.ParameterType.Name})"))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "the dev seeders write through the owning sections' services, never their data");
    }

    [HumansFact]
    public void Register_binds_the_three_dev_seeders_outside_production()
    {
        var services = Register(environmentName: Environments.Development);

        services.Select(d => d.ServiceType.Name).Order(StringComparer.Ordinal).Should().BeEquivalentTo(
            ["DevPersonaSeeder", "DevelopmentCampRoleSeeder", "DevelopmentDashboardSeeder"]);
    }

    [HumansFact]
    public void Register_binds_nothing_in_production_or_when_the_environment_is_unknown()
    {
        // The whole point of the section's Register. DevLoginController takes DevPersonaSeeder
        // as a constructor dependency, so an unregistered seeder is what makes Shell's
        // DevLoginControllerExclusionProvider load-bearing rather than decorative — and a dev
        // seeder that reached Production could mint Identity accounts.
        //
        // The unknown case is asserted deliberately: Register reads the environment out of the
        // configuration Shell hands it (ISection.Register takes no IHostEnvironment), so it
        // fails closed. If the host ever stops surfacing HostDefaults.EnvironmentKey, dev login
        // 404s in the test host — loud — instead of three seeders reaching prod — silent.
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
