using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Web.Tests.Services.Gdpr;

/// <summary>
/// Architecture tests for GDPR-export contributor wiring. These prevent the
/// silent-omission bug the whole refactor exists to eliminate: when a new
/// user-scoped section is added and its owning service forgets to register
/// its <see cref="IUserDataContributor"/> implementation in DI, the export
/// would drop that category without warning. These tests fail loudly
/// instead.
///
/// <para>
/// The contributor roster is derived entirely from reflection over the
/// section assemblies the runtime composes itself from — never a pinned
/// type list — so a spoke that implements the interface is automatically
/// in scope and a spoke that moves or renames cannot silently drop out.
/// </para>
/// </summary>
public class GdprExportDependencyInjectionTests
{
    /// <summary>
    /// Every <see cref="IUserDataContributor"/> implementation found by reflection
    /// over the section assemblies the runtime composes itself from, plus the host
    /// assembly (residue that hasn't moved into a section project). This is the
    /// ground truth the tests below check DI registration against — no section is
    /// named here, so a new contributor joins the roster the moment it implements
    /// the interface.
    /// </summary>
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
        // Guards the rest of the class against passing vacuously if section
        // discovery ever returns nothing.
        DiscoverContributorTypes().Should().NotBeEmpty(
            "GDPR export DI coverage is enforced by reflecting over the composed section assemblies");
    }

    [HumansFact]
    public void EveryDiscoveredContributorIsRegisteredInInfrastructure()
    {
        // Walk the real InfrastructureServiceCollectionExtensions registrations
        // and verify each discovered contributor appears as an IUserDataContributor
        // forwarding factory. We read the collection's ServiceDescriptors directly
        // so the test doesn't need a live DbContext, Postgres, or config.
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

        // Each IUserDataContributor registration is a factory that forwards to
        // the concrete section service. We can't introspect the factory body,
        // but we CAN verify that for every discovered contributor type, its
        // concrete-type registration exists AND exactly one IUserDataContributor
        // factory is wired alongside it.
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
        // This is the "prevent silent drop" assertion. Counting descriptors
        // alone doesn't catch the bug where one contributor's factory is
        // duplicated and another is omitted — count still matches. Here we
        // actually invoke the real forwarding factories via a test
        // ServiceProvider whose concrete-type registrations are replaced with
        // `GetUninitializedObject` fakes. Each factory resolves its target
        // concrete type, and the set of resolved types must exactly match the
        // discovered roster.
        var expectedContributorTypes = DiscoverContributorTypes();

        var services = new ServiceCollection();
        var config = BuildMinimalConfiguration();
        Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
                new StubHostEnvironment());

        // Replace every contributor's concrete-type registration with a fake
        // instance of that same type. GetUninitializedObject skips the
        // constructor, so we never touch DbContext, IClock, or any of the
        // other runtime dependencies.
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
