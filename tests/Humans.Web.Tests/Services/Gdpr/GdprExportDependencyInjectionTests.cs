using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using ProfilesAccountMergeService = Humans.Users.Services.AccountMergeService;
using UsersUserService = Humans.Users.Services.UserService;
using TeamService = Humans.Teams.Services.TeamService;

namespace Humans.Web.Tests.Services.Gdpr;

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
        typeof(UsersUserService),
        typeof(ProfilesAccountMergeService),
        SectionType("Humans.Governance.Services.ApplicationDecisionService"),
        SectionType("Humans.Consent.Services.ConsentService"),
        typeof(TeamService),
        SectionType("Humans.Auth.Services.RoleAssignmentService"),
        SectionType("Humans.Shifts.Services.ShiftSignupService"),
        SectionType("Humans.Feedback.Services.FeedbackService"),
        SectionType("Humans.Issues.Services.IssuesService"),
        SectionType("Humans.Notifications.Services.NotificationInboxService"),
        SectionType("Humans.Tickets.Services.TicketQueryService"),
        SectionType("Humans.Campaigns.Services.CampaignService"),
        SectionType("Humans.Camps.Services.CampService"),
        SectionType("Humans.Events.Services.EventService"),
        SectionType("Humans.AuditLog.Services.AuditLogService"),
        SectionType("Humans.Budget.Services.BudgetService"),
        SectionType("Humans.Agent.Services.AgentService"),
        SectionType("Humans.Expenses.Services.ExpenseReportService"),
        SectionType("Humans.Finance.Services.Service"),
        SectionType("Humans.Surveys.Services.SurveyService"),
        SectionType("Humans.Gate.Services.GateService")
    ];

    /// <summary>
    /// A G5 section's service is <c>internal</c> to its own assembly
    /// (nobodies-collective/Humans#866), so it cannot be named with <c>typeof</c> here.
    /// Resolved by reflection instead, which keeps the section in the expected-contributor
    /// list rather than dropping it — the silent-omission bug this class exists to prevent.
    /// </summary>
    private static Type SectionType(string fullName) =>
        Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

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
        // Scan every assembly where section services live: Humans.Infrastructure
        // still holds most of them, Humans.Application is the intermediate target
        // per the repository/store/decorator migration (first move:
        // ApplicationDecisionService, Governance PR #503, since moved to G5), and each G5 section
        // project (nobodies-collective/Humans#866) holds its own. The section
        // assemblies come from SectionDiscoveryExtensions — the same discovery the
        // runtime uses, so a section that moves cannot silently drop out of this
        // sweep the way it would with a hard-coded assembly list (design §10).
        // Humans.Infrastructure was the first entry until G5 lane 5b-6 deleted it; its residue
        // (and, at 5c, Dashboard's) lands in Humans.Web, so the host assembly takes its place.
        var hostAssembly = typeof(Web.Extensions.InfrastructureServiceCollectionExtensions).Assembly;
        var applicationAssembly = typeof(Humans.Users.Services.UserService).Assembly;

        var foundContributors = new[] { hostAssembly, applicationAssembly }
            .Concat(Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies())
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
        Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
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

    [HumansFact]
    public void GdprExportServiceIsRegistered()
    {
        var services = new ServiceCollection();
        Web.Extensions.InfrastructureServiceCollectionExtensions
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
        Web.Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(
                services,
                config,
                new StubHostEnvironment());

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
