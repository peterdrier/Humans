using AwesomeAssertions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Application.Tests.Services.Gdpr;

/// <summary>
/// Architecture tests for GDPR-export contributor wiring. These prevent the
/// silent-omission bug the whole refactor exists to eliminate: when a new
/// user-scoped section is added and its owning service forgets to implement
/// <see cref="IUserDataContributor"/> (or forgets to register it in DI), the
/// export would drop that category without warning. These tests fail loudly
/// instead.
/// </summary>
public class GdprExportDependencyInjectionTests
{
    /// <summary>
    /// Every section service that owns user-scoped tables MUST appear here.
    /// Update this list when a new user-scoped section is introduced. If a
    /// contributor is added to <c>Humans.Infrastructure</c> but not added
    /// here, <see cref="EveryIUserDataContributorInInfrastructureIsExpected"/>
    /// will fail.
    /// </summary>
    public static readonly Type[] ExpectedContributorTypes =
    [
        typeof(ProfileService),
        typeof(UserService),
        typeof(AccountMergeService),
        typeof(ApplicationDecisionService),
        typeof(ConsentService),
        typeof(TeamService),
        typeof(RoleAssignmentService),
        typeof(ShiftSignupService),
        typeof(FeedbackService),
        typeof(NotificationInboxService),
        typeof(TicketQueryService),
        typeof(CampaignService),
        typeof(CampService),
        typeof(AuditLogService),
        typeof(BudgetService)
    ];

    [Fact]
    public void EverySectionServiceMustImplementIUserDataContributor()
    {
        foreach (var type in ExpectedContributorTypes)
        {
            typeof(IUserDataContributor).IsAssignableFrom(type)
                .Should().BeTrue(
                    $"{type.Name} owns user-scoped tables and must implement IUserDataContributor for the GDPR export orchestrator");
        }
    }

    [Fact]
    public void EveryIUserDataContributorInInfrastructureIsExpected()
    {
        var infrastructureAssembly = typeof(ProfileService).Assembly;
        var foundContributors = infrastructureAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IUserDataContributor).IsAssignableFrom(t))
            .ToArray();

        foundContributors.Should().BeEquivalentTo(
            ExpectedContributorTypes,
            "every IUserDataContributor implementation must be accounted for in ExpectedContributorTypes — add new contributors to that list");
    }

    [Fact]
    public void EveryExpectedContributorIsRegisteredInInfrastructure()
    {
        // Walk the real InfrastructureServiceCollectionExtensions registrations
        // and verify each expected contributor appears as an IUserDataContributor
        // forwarding factory. We read the collection's ServiceDescriptors directly
        // so the test doesn't need a live DbContext, Postgres, or config.
        var services = new ServiceCollection();
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                BuildMinimalConfiguration(),
                new StubHostEnvironment());

        var contributorDescriptors = services
            .Where(d => d.ServiceType == typeof(IUserDataContributor))
            .ToArray();

        contributorDescriptors.Should().HaveCount(ExpectedContributorTypes.Length,
            "every expected contributor must have exactly one IUserDataContributor registration");

        // Each IUserDataContributor registration is a factory that forwards to
        // the concrete section service. We can't introspect the factory body,
        // but we CAN verify that for every expected contributor type, its
        // concrete-type registration exists AND exactly one IUserDataContributor
        // factory is wired alongside it.
        foreach (var expected in ExpectedContributorTypes)
        {
            services.Should().ContainSingle(d => d.ServiceType == expected,
                $"{expected.Name} must be registered as its own concrete type so the IUserDataContributor factory can forward to it");
        }
    }

    [Fact]
    public void GdprExportServiceIsRegistered()
    {
        var services = new ServiceCollection();
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                BuildMinimalConfiguration(),
                new StubHostEnvironment());

        services.Should().ContainSingle(d => d.ServiceType == typeof(IGdprExportService),
            "the GDPR export orchestrator must be registered exactly once");
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
