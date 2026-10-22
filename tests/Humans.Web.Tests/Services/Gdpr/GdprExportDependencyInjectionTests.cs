using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Services.Gdpr;

/// <summary>
/// A contributor missing from DI would silently drop its category from the export. The roster comes from reflection,
/// never a pinned list, so a new contributor is in scope the moment it implements the interface.
/// </summary>
public class GdprExportDependencyInjectionTests
{
    private static Type[] DiscoverContributorTypes()
    {
        var hostAssembly = typeof(Extensions.InfrastructureServiceCollectionExtensions).Assembly;
        var applicationAssembly = typeof(Humans.Users.Services.UserService).Assembly;

        return new[] { hostAssembly, applicationAssembly }
            .Concat(Extensions.SectionDiscoveryExtensions.SectionAssemblies())
            .Distinct()
            .SelectMany(asm => asm.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IUserDataContributor).IsAssignableFrom(t))
            .Distinct()
            .ToArray();
    }

    [HumansFact]
    public void ContributorsAreDiscoverable()
    {
        DiscoverContributorTypes().Should().NotBeEmpty(
            "GDPR export DI coverage is enforced by reflecting over the composed section assemblies");
    }

    [HumansFact]
    public void EveryDiscoveredContributorIsRegisteredInInfrastructure()
    {
        // Reads the ServiceDescriptors directly, so no DbContext, Postgres or config is needed.
        var expectedContributorTypes = DiscoverContributorTypes();

        var services = new ServiceCollection();
        var config = BuildMinimalConfiguration();
        Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
                new StubHostEnvironment());

        var contributorDescriptors = services
            .Where(d => d.ServiceType == typeof(IUserDataContributor))
            .ToArray();

        contributorDescriptors.Should().HaveCount(expectedContributorTypes.Length,
            "every discovered contributor must have exactly one IUserDataContributor registration");

        // The forwarding factory body can't be inspected, so check each concrete registration has exactly one
        // IUserDataContributor factory beside it.
        foreach (var expected in expectedContributorTypes)
        {
            services.Should().ContainSingle(d => d.ServiceType == expected,
                $"{expected.Name} must be registered as its own concrete type so the IUserDataContributor factory can forward to it");
        }
    }

    [HumansFact]
    public void GdprServiceIsRegistered()
    {
        var services = new ServiceCollection();
        Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                BuildMinimalConfiguration(),
                new StubHostEnvironment());

        services.Should().ContainSingle(d => d.ServiceType == typeof(IGdprService),
            "the GDPR subject-rights orchestrator must be registered exactly once");
    }

    [HumansFact]
    public void EveryIUserDataContributorFactoryForwardsToADistinctDiscoveredConcreteType()
    {
        // Counting descriptors misses one factory duplicated and another omitted, so invoke the real factories against
        // uninitialized fakes and compare the resolved types with the roster.
        var expectedContributorTypes = DiscoverContributorTypes();

        var services = new ServiceCollection();
        var config = BuildMinimalConfiguration();
        Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
                new StubHostEnvironment());

        // GetUninitializedObject skips the constructor, so no runtime dependency is touched.
        foreach (var type in expectedContributorTypes)
        {
            var existing = services.FirstOrDefault(d =>
                d.ServiceType == type && d.ImplementationFactory is null);
            if (existing is not null)
            {
                services.Remove(existing);
            }
            var fake = RuntimeHelpers.GetUninitializedObject(type);
            services.AddScoped(type, _ => fake);
        }

        using var provider = services.BuildServiceProvider(validateScopes: false);
        using var scope = provider.CreateScope();

        var resolvedTypes = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IUserDataContributor>>()
            .Select(c => c.GetType())
            .ToArray();

        resolvedTypes.Should().BeEquivalentTo(
            expectedContributorTypes,
            "every IUserDataContributor forwarding factory must resolve to a distinct discovered concrete type — duplicated or mis-forwarded factories would silently drop a section");
    }

    private static IConfiguration BuildMinimalConfiguration()
    {
        var inMemory = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["Email:FromAddress"] = "humans@nobodies.team",
            ["Email:BaseUrl"] = "https://localhost",
            ["Email:SmtpHost"] = "localhost",
            ["GitHub:Owner"] = "stub",
            ["GitHub:Repository"] = "stub",
            ["GitHub:AccessToken"] = "stub",
            ["GoogleMaps:ApiKey"] = "stub",
            ["TicketVendor:EventId"] = "stub-event",
            ["TicketVendor:Provider"] = "stub"
        };

        var builder = new ConfigurationBuilder();
        builder.Add(new MemoryConfigurationSource { InitialData = inMemory });
        return builder.Build();
    }

    private sealed class StubHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Humans.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
