using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Architecture.Ratchet;
using Xunit;
using NotificationService = Humans.Application.Services.Notifications.NotificationService;
using NotificationInboxService = Humans.Application.Services.Notifications.NotificationInboxService;
using NotificationMeterProvider = Humans.Application.Services.Notifications.NotificationMeterProvider;
using Humans.Infrastructure.Repositories.Notifications;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the
/// Notifications section — migrated per issue #550.
///
/// <para>
/// Notifications chose <b>Option A</b> (no caching decorator, no dict cache):
/// in-app dispatch is fire-and-forget and reads go through the inbox service
/// whose nav-badge counts are already cached at the view-component layer via
/// short-TTL <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>.
/// The same rationale used by Users (#243), Governance (#242), Budget (#544),
/// City Planning (#543), and Audit Log (#552) when they skipped the decorator.
/// </para>
/// </summary>
public class NotificationsArchitectureTests
{
    // ── NotificationService ──────────────────────────────────────────────────

    // The DbContext-constructor-parameter check is covered by the generic
    // ApplicationServicesTakeNoDbContextRule for every Application service.

    [HumansFact]
    public void NotificationService_TakesRepository()
    {
        var ctor = typeof(NotificationService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INotificationRepository));
    }

    [HumansFact]
    public void NotificationService_TakesRecipientResolver_NotDbContext()
    {
        // The NotificationService reaches teams and role holders via a thin
        // recipient-resolver adapter rather than directly injecting
        // ITeamService/IRoleAssignmentService — those services inject
        // INotificationService in the other direction, so a direct dependency
        // here closes a circular DI graph that trips ValidateOnBuild at
        // startup. The resolver exists solely to break that cycle.
        var ctor = typeof(NotificationService).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        paramTypeNames.Should().Contain("INotificationRecipientResolver");
        paramTypeNames.Should().NotContain("ITeamService");
        paramTypeNames.Should().NotContain("IRoleAssignmentService");
    }

    // ── NotificationInboxService ─────────────────────────────────────────────

    [HumansFact]
    public void NotificationInboxService_TakesRepositoryAndUserService()
    {
        // Display-name stitching runs through IUserService.GetByIdsAsync rather
        // than a cross-domain .Include(nr => nr.User) chain (design-rules §6).
        var ctor = typeof(NotificationInboxService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INotificationRepository));
        paramTypes.Should().Contain(p => p.Name == "IUserService");
    }

    // ── NotificationMeterProvider ────────────────────────────────────────────

    [HumansFact]
    public void NotificationMeterProvider_LivesInHumansApplicationServicesNotificationsNamespace()
    {
        typeof(NotificationMeterProvider).Namespace
            .Should().Be("Humans.Application.Services.Notifications");
    }

    [HumansFact]
    public void NotificationMeterProvider_TakesCrossSectionInterfaces()
    {
        // The meter provider computes badge counts by calling into each owning
        // section service (IProfileService, IUserService, IGoogleSyncService,
        // ITeamService, ITicketSyncService, IApplicationDecisionService,
        // ICampService) — never reading the underlying tables directly.
        var ctor = typeof(NotificationMeterProvider).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        paramTypeNames.Should().Contain("IProfileService");
        paramTypeNames.Should().Contain("IUserService");
        paramTypeNames.Should().Contain("IGoogleSyncService");
        paramTypeNames.Should().Contain("ITeamService");
        paramTypeNames.Should().Contain("ITicketSyncService");
        paramTypeNames.Should().Contain("IApplicationDecisionService");
        paramTypeNames.Should().Contain("ICampService");
    }

    [HumansFact]
    public void NotificationMeterProvider_TakesNoRepositoryDependency()
    {
        // The meter provider does not own notifications/notification_recipients
        // reads either — those stay with the inbox service. It is purely an
        // aggregator across other sections' count methods.
        var ctor = typeof(NotificationMeterProvider).GetConstructors().Single();
        var hasRepo = ctor.GetParameters()
            .Any(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        hasRepo.Should().BeFalse(
            because: "the meter provider is a cross-section aggregator; it should not bypass any section's public service interface (design-rules §9)");
    }

    // ── INotificationRepository ──────────────────────────────────────────────

    // Sealed-repository check is covered by the generic
    // IRepositoryImplementationsAreSealedRule across every repository.

    // ── Sole-writer DbSet rule ───────────────────────────────────────────────

    /// <summary>
    /// Only <c>NotificationRepository</c> may write to
    /// <c>ctx.Notifications</c> or <c>ctx.NotificationRecipients</c>. Any
    /// other production class calling <c>.Add</c>, <c>.AddRange</c>,
    /// <c>.Update</c>, <c>.Remove</c>, or <c>.Attach</c> on either DbSet is
    /// a cross-section boundary violation: callers must go through
    /// <see cref="Humans.Application.Interfaces.Notifications.INotificationService"/>,
    /// <see cref="Humans.Application.Interfaces.Notifications.INotificationEmitter"/>,
    /// or <see cref="Humans.Application.Interfaces.Notifications.INotificationInboxService"/>.
    /// </summary>
    [HumansFact]
    public void Only_NotificationRepository_Writes_Notification_DbSets()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = ScanNotificationDbSetWrites(repoRoot);
        RatchetTestRunner.Run(
            "OnlyNotificationRepositoryWritesNotificationDbSets",
            "tests/Humans.Application.Tests/Architecture/Baselines/OnlyNotificationRepositoryWritesNotificationDbSets.baseline.txt",
            violations);
    }

    // Matches the write-operation call chains on either notification DbSet.
    // e.g. ctx.Notifications.Add(...)  /  .AddRange  /  .Update  /  .Remove  /  .Attach
    //     ctx.NotificationRecipients.Add(...)  / ... etc.
    private static readonly Regex NotificationWriteRegex = new(
        @"(?:Notifications|NotificationRecipients)\s*\.\s*(?:Add|AddRange|Update|Remove|Attach)\b",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    internal static IEnumerable<string> ScanNotificationDbSetWrites(string repoRoot)
    {
        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            // The canonical owner is NotificationRepository — exclude it from violation reporting.
            if (path.Replace('\\', '/').EndsWith(
                    "Infrastructure/Repositories/Notifications/NotificationRepository.cs",
                    StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            if (!NotificationWriteRegex.IsMatch(content)) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in NotificationWriteRegex.Matches(content).Cast<System.Text.RegularExpressions.Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                var dbset = match.Value.StartsWith("NotificationRecipients", StringComparison.Ordinal)
                    ? "NotificationRecipients-write"
                    : "Notifications-write";
                yield return $"{rel}:{dbset}#{ordinal} # L{line}";
            }
        }
    }
}
