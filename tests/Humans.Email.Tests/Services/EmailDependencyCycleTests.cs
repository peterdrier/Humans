using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.Users.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Services;
using Humans.Notifications.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests.Services;

/// <summary>
/// The real send chain — <c>UserService</c> → <c>UserEmailService</c> →
/// <c>OutboxEmailService</c> → back to <c>IUserEmailService</c> — resolves without a
/// constructor cycle.
/// </summary>
/// <remarks>
/// Lived in <c>Humans.Application.Tests</c>' <c>DependencyCycleResolutionTests</c> until
/// the section's G5 move made <c>OutboxEmailService</c> internal to another assembly
/// (CityPlanning finding 18's shape). It belongs here now: the section owns the concrete
/// type, and the Base half of the same guard — <c>IUserService</c> resolving with a
/// substituted <c>IEmailService</c> — stayed where it was.
/// </remarks>
public sealed class EmailDependencyCycleTests
{
    [HumansFact]
    public void IUserService_And_IEmailService_Resolve_WhenRealEmailChainIsRegistered()
    {
        var services = new ServiceCollection();
        var userStore = Substitute.For<IUserStore<User>>();

        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddScoped<IUserInfoInvalidator>(_ => Substitute.For<IUserInfoInvalidator>());
        services.AddScoped<IAuditLogService>(_ => Substitute.For<IAuditLogService>());
        services.AddScoped<INotificationEmitter>(_ => Substitute.For<INotificationEmitter>());
        services.AddScoped<ISystemTeamSync>(_ => Substitute.For<ISystemTeamSync>());
        services.AddScoped<INavBadgeCacheInvalidator>(_ => Substitute.For<INavBadgeCacheInvalidator>());
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator>(_ => Substitute.For<IRoleAssignmentClaimsCacheInvalidator>());
        services.AddScoped<INotificationMeterCacheInvalidator>(_ => Substitute.For<INotificationMeterCacheInvalidator>());
        services.AddScoped<IShiftAuthorizationInvalidator>(_ => Substitute.For<IShiftAuthorizationInvalidator>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<IEmailOutboxRepository>(_ => Substitute.For<IEmailOutboxRepository>());
        services.AddScoped<IEmailBodyComposer>(_ => Substitute.For<IEmailBodyComposer>());
        services.AddScoped<IImmediateOutboxProcessor>(_ => Substitute.For<IImmediateOutboxProcessor>());
        services.AddScoped<IHumansMetrics>(_ => Substitute.For<IHumansMetrics>());
        services.AddScoped<ICommunicationPreferenceService>(_ => Substitute.For<ICommunicationPreferenceService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());
        services.AddScoped<UserManager<User>>(_ =>
            Substitute.For<UserManager<User>>(userStore, null, null, null, null, null, null, null, null));

        // Users is another section; its concrete UserService and its two repository
        // interfaces are internal to Humans.Users and its own graph is pinned by the
        // section's own tests. Same call as ITeamService / IRoleAssignmentService /
        // IShiftManagementServiceRead below — the subject here is the email chain.
        services.AddScoped<IUserService>(_ => Substitute.For<IUserService>());

        // Auth is another section; RoleAssignmentService is internal to Humans.Auth and its
        // own constructor shape is pinned by AuthArchitectureTests. Same call as ITeamService
        // below — the subject here is the email chain.
        services.AddScoped<IRoleAssignmentService>(_ => Substitute.For<IRoleAssignmentService>());

        // Shifts is another section; its concrete service is internal to Humans.Shifts and
        // its own graph is pinned by the section's own tests. Same call as ITeamService and
        // IRoleAssignmentService below — the subject here is the email chain.
        services.AddScoped<IShiftManagementServiceRead>(_ => Substitute.For<IShiftManagementServiceRead>());

        services.AddScoped<IUserEmailService>(_ => Substitute.For<IUserEmailService>());

        // Section.cs picks Smtp or Stub by configuration; either way it is Scoped, so the
        // send path OutboxEmailService takes for DoNotPersist messages resolves.
        services.AddScoped<IEmailTransport>(_ => Substitute.For<IEmailTransport>());

        services.AddScoped<OutboxEmailService>();
        services.AddScoped<IEmailService>(sp => sp.GetRequiredService<OutboxEmailService>());

        // Teams is another section; its concrete service is internal to Humans.Teams and its
        // own cycle is pinned by TeamsDependencyCycleTests. The subject here is the email chain.
        services.AddScoped<ITeamService>(_ => Substitute.For<ITeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<OutboxEmailService>>(_ => NullLogger<OutboxEmailService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolveUserService = () => scope.ServiceProvider.GetRequiredService<IUserService>();
        var resolveEmailService = () => scope.ServiceProvider.GetRequiredService<IEmailService>();

        resolveUserService.Should().NotThrow();
        resolveEmailService.Should().NotThrow();
        resolveEmailService().Should().BeOfType<OutboxEmailService>();
    }

    /// <summary>
    /// A crosscut may not grow new edges into sections. Both sets are the ones the
    /// teardown of peterdrier/Humans#1651 shrinks — Email.Contracts is already down to
    /// Base and Users.Contracts — so these assertions exist to stop a new reference
    /// arriving meanwhile, not to bless what is here.
    /// Asserted from the csproj's own &lt;ProjectReference&gt; items, not
    /// <c>Assembly.GetReferencedAssemblies()</c>: a const-only or unused reference emits
    /// no metadata reference and would pass that check silently (see
    /// <c>Humans.Tickets.Contracts</c> below — used only for a <c>const string</c>).
    /// </summary>
    [HumansFact]
    public void EmailContracts_ReferencesOnlyBaseAndUsersContracts() =>
        ProjectReferencesOf("Humans.Email.Contracts").Should().BeSubsetOf(
            ["Humans.Base", "Humans.Users.Contracts"],
            because: "Email is a crosscut: its contracts leaf may lose section references, never gain one");

    [HumansFact]
    public void Email_ReferencesOnlyItsCurrentSectionContracts() =>
        ProjectReferencesOf("Humans.Email").Should().BeSubsetOf(
            [
                "Humans.AuditLog.Contracts", "Humans.Base", "Humans.Campaigns.Contracts", "Humans.Email.Contracts",
                "Humans.Gdpr.Contracts", "Humans.Settings.Contracts",
                "Humans.Tickets.Contracts", "Humans.Users.Contracts"
            ],
            because: "Email is a crosscut: it may lose section references, never gain one");

    private static IEnumerable<string> ProjectReferencesOf(string projectName)
    {
        var csprojPath = Path.Combine(LocateRepoRoot(), "src", "Sections", projectName, projectName + ".csproj");
        var xml = System.Xml.Linq.XDocument.Load(csprojPath);
        return xml.Descendants("ProjectReference")
            .Select(e => ((string)e.Attribute("Include")!).Split('\\', '/')[^1])
            .Select(name => name[..^".csproj".Length])
            .Distinct(StringComparer.Ordinal);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repository root (no Humans.slnx above " + AppContext.BaseDirectory + ").");
    }
}
