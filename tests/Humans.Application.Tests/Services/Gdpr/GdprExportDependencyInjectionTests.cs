using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Profiles;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ApplicationDecisionService = Humans.Application.Services.Governance.ApplicationDecisionService;
using BudgetService = Humans.Application.Services.Budget.BudgetService;
using CampaignService = Humans.Application.Services.Campaigns.CampaignService;
using ProfilesProfileService = Humans.Application.Services.Profile.ProfileService;
using ProfilesAccountMergeService = Humans.Application.Services.Profile.AccountMergeService;
using UsersUserService = Humans.Application.Services.Users.UserService;
using AuditLogService = Humans.Application.Services.AuditLog.AuditLogService;
using CampService = Humans.Application.Services.Camps.CampService;
using FeedbackService = Humans.Application.Services.Feedback.FeedbackService;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;
using ConsentService = Humans.Application.Services.Consent.ConsentService;
using ShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;
using TicketsTicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;
using NotificationInboxService = Humans.Application.Services.Notifications.NotificationInboxService;
using TeamService = Humans.Application.Services.Teams.TeamService;

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
    /// This list is the enforced view of the §8 Table Ownership Map in
    /// <c>docs/architecture/design-rules.md</c> — when adding a new section to
    /// §8 whose tables hold per-user rows, ALSO add its owning service type
    /// here. The tests below use this list to prove two invariants:
    ///
    /// <list type="number">
    /// <item><description>
    /// Every type in this list actually implements
    /// <see cref="IUserDataContributor"/>
    /// (<see cref="EverySectionServiceMustImplementIUserDataContributor"/>).
    /// </description></item>
    /// <item><description>
    /// Every <see cref="IUserDataContributor"/> implementation found by
    /// reflection in the <c>Humans.Infrastructure</c> assembly is accounted
    /// for in this list
    /// (<see cref="EveryIUserDataContributorInInfrastructureIsExpected"/>) —
    /// so you can't add a new contributor without registering it here.
    /// </description></item>
    /// <item><description>
    /// Every listed type is registered in DI as both its concrete type and a
    /// forwarding <see cref="IUserDataContributor"/> factory
    /// (<see cref="EveryExpectedContributorIsRegisteredInInfrastructure"/>
    /// and <see cref="EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType"/>).
    /// </description></item>
    /// </list>
    ///
    /// <b>Uncaught case:</b> If a new user-scoped section is added to §8 but
    /// its owning service never implements <see cref="IUserDataContributor"/>
    /// in the first place, reflection finds nothing to enumerate and the tests
    /// pass vacuously. The §8a cross-cutting note in <c>design-rules.md</c>
    /// is the prose-level guardrail against that.
    /// </summary>
    public static readonly Type[] ExpectedContributorTypes =
    [
        typeof(ProfilesProfileService),
        typeof(UsersUserService),
        typeof(ProfilesAccountMergeService),
        typeof(ApplicationDecisionService),
        typeof(ConsentService),
        typeof(TeamService),
        typeof(RoleAssignmentService),
        typeof(ShiftSignupService),
        typeof(FeedbackService),
        typeof(NotificationInboxService),
        typeof(TicketsTicketQueryService),
        typeof(CampaignService),
        typeof(CampService),
        typeof(AuditLogService),
        typeof(BudgetService),
        typeof(Humans.Infrastructure.Services.Agent.AgentService)
    ];

    [HumansFact]
    public void EverySectionServiceMustImplementIUserDataContributor()
    {
        foreach (var type in ExpectedContributorTypes)
        {
            typeof(IUserDataContributor).IsAssignableFrom(type)
                .Should().BeTrue(
                    $"{type.Name} owns user-scoped tables and must implement IUserDataContributor for the GDPR export orchestrator");
        }
    }

    [HumansFact]
    public void EveryIUserDataContributorInInfrastructureIsExpected()
    {
        // Scan both assemblies where section services live: Humans.Infrastructure
        // still holds most of them, and Humans.Application is the new target
        // location per the repository/store/decorator migration — the first
        // such move is ApplicationDecisionService (Governance, PR #503).
        var infrastructureAssembly = typeof(TeamService).Assembly;
        var applicationAssembly = typeof(ApplicationDecisionService).Assembly;

        var foundContributors = new[] { infrastructureAssembly, applicationAssembly }
            .SelectMany(asm => asm.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IUserDataContributor).IsAssignableFrom(t))
            .Distinct()
            .ToArray();

        foundContributors.Should().BeEquivalentTo(
            ExpectedContributorTypes,
            "every IUserDataContributor implementation must be accounted for in ExpectedContributorTypes — add new contributors to that list");
    }

    [HumansFact]
    public void EveryExpectedContributorIsRegisteredInInfrastructure()
    {
        // Walk the real InfrastructureServiceCollectionExtensions registrations
        // and verify each expected contributor appears as an IUserDataContributor
        // forwarding factory. We read the collection's ServiceDescriptors directly
        // so the test doesn't need a live DbContext, Postgres, or config.
        var services = new ServiceCollection();
        var config = BuildMinimalConfiguration();
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
                new StubHostEnvironment());
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddAgentSection(services, config);

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

    [HumansFact]
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

    [HumansFact]
    public void EveryIUserDataContributorFactoryForwardsToAnExpectedConcreteType()
    {
        // This is the "prevent silent drop" assertion. Counting descriptors
        // alone doesn't catch the bug where one contributor's factory is
        // duplicated and another is omitted — count still matches. Here we
        // actually invoke the real forwarding factories via a test
        // ServiceProvider whose concrete-type registrations are replaced with
        // `GetUninitializedObject` fakes. Each factory resolves its target
        // concrete type, and the set of resolved types must exactly match
        // `ExpectedContributorTypes`.
        var services = new ServiceCollection();
        var config = BuildMinimalConfiguration();
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
                new StubHostEnvironment());
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddAgentSection(services, config);

        // Replace every contributor's concrete-type registration with a fake
        // instance of that same type. GetUninitializedObject skips the
        // constructor, so we never touch DbContext, IClock, or any of the
        // other runtime dependencies.
        foreach (var type in ExpectedContributorTypes)
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
            ExpectedContributorTypes,
            "every IUserDataContributor forwarding factory must resolve to a distinct expected concrete type — duplicated or mis-forwarded factories would silently drop a section");
    }

    [HumansFact]
    public void IFullProfileInvalidator_IsRegisteredAsFactory_NotAFreshInstance()
    {
        // Verifies the critical DI invariant at the descriptor level:
        // IFullProfileInvalidator must be registered as a factory (not as a
        // direct type mapping to CachingProfileService), so the same decorator
        // instance that backs IProfileService within a scope also backs
        // IFullProfileInvalidator. Resolving IProfileService to call BeSameAs is
        // not feasible here because it requires HumansDbContext — this
        // descriptor-level check is the best guard available without a full
        // integration test harness.
        //
        // The "same instance" invariant is documented by the CRITICAL comment at
        // the registration site in InfrastructureServiceCollectionExtensions.cs
        // and is enforced in unit tests in CachingProfileServiceTests.cs.
        var services = new ServiceCollection();
        Humans.Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                BuildMinimalConfiguration(),
                new StubHostEnvironment());

        var descriptor = services.Single(d => d.ServiceType == typeof(IFullProfileInvalidator));

        descriptor.ImplementationFactory.Should().NotBeNull(
            "IFullProfileInvalidator must be registered as a factory that delegates to " +
            "IProfileService — a direct type registration would create a separate " +
            "CachingProfileService instance with its own ConcurrentDictionary");

        descriptor.ImplementationType.Should().BeNull(
            "IFullProfileInvalidator must not be registered with a direct type mapping; " +
            "it must use a factory that shares the IProfileService decorator instance");
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
