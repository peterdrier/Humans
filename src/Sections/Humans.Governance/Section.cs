using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Google.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Jobs;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Governance.ViewComponents;
using Humans.Issues.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;
using Humans.Governance.Controllers;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Governance;

/// <summary>
/// Governance's DI entry point. No caching decorator: at a handful of Board-driven writes a
/// week the service reads through the repository per request and invalidates the nav /
/// notification-meter / voting-badge caches inline after a write.
/// </summary>
public sealed class Section :
    SectionBase,
    IIssueQueueOwner,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionChrome,
    ISectionDataMigrations,
    ISectionHelp,
    ISectionJobs,
    ISectionMemberDashboard,
    ISectionThingsToDo,
    IUserPart
{
    IEnumerable<UserPart> IUserPart.Parts() =>
        [new(UserPartSlots.DashboardHeading, typeof(MembershipTierBadgeViewComponent))];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, GovernanceAuditActions>();
        services.AddSingleton<INotificationSourceContributor, GovernanceNotificationSources>();
        services.AddScoped<INotificationMeterContributor, GovernanceNotificationMeters>();
        services.AddSectionDbContext<GovernanceDbContext>(sentinelTable: "applications");

        services.AddSingleton<IApplicationRepository, ApplicationRepository>();

        services.AddSingleton<IMembershipTierRepository>(sp => (IMembershipTierRepository)sp.GetRequiredService<IApplicationRepository>());
        services.AddScoped<MembershipTierService>();
        services.AddKeyedScoped<IGovernanceService>(CachingGovernanceService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<MembershipTierService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MembershipTierService>());
        // Snapshot legacy tier grants before ApplicationDecisionService moves their applications.
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<MembershipTierService>());
        services.AddSingleton<CachingGovernanceService>();
        services.AddSingleton<IGovernanceService>(sp => sp.GetRequiredService<CachingGovernanceService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingGovernanceService>());

        services.AddScoped<ApplicationDecisionService>();
        services.AddKeyedScoped<IGovernanceManagement>(CachingGovernanceManagement.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddSingleton<CachingGovernanceManagement>();
        services.AddSingleton<IGovernanceManagement>(sp => sp.GetRequiredService<CachingGovernanceManagement>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingGovernanceManagement>());

        services.AddScoped<IGovernanceIndexService, GovernanceIndexService>();

        services.AddSingleton<IGovernanceBodyRepository, GovernanceBodyRepository>();
        services.AddScoped<GovernanceBodyLinksService>();
        services.AddScoped<GovernanceBodyRosters>();
        services.AddScoped<GovernanceBodySync>();
        services.AddScoped<GovernanceBodyGoogleSource>();
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<GovernanceBodyGoogleSource>());
        services.AddScoped<IGoogleDriveAccessSource>(sp => sp.GetRequiredService<GovernanceBodyGoogleSource>());
        services.AddScoped<GovernanceBoardSeats>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GovernanceBoardSeats>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GovernanceBoardSeats>());

        // Assembly votes. The service is the embargo boundary, so nothing here exposes the
        // repository outside the section and no caching decorator wraps it — a cached tally
        // is a leaked tally.
        services.AddSingleton<IAssemblyVoteRepository, AssemblyVoteRepository>();
        services.AddScoped<AssemblyVoteService>();
        services.AddScoped<IAssemblyVoteService>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<AssemblyVoteLapseJob>();


        // Base cannot name a moved section's enums, so the section pushes its badge rows into
        // the Humans.UI registry rather than Humans.UI referencing this section.
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [ApplicationStatus.Submitted] = "bg-primary",
            [ApplicationStatus.Approved] = "bg-success",
            [ApplicationStatus.Rejected] = "bg-danger",

            [VoteChoice.Yay] = "bg-success",
            [VoteChoice.Maybe] = "bg-warning text-dark",
            [VoteChoice.No] = "bg-danger",
            [VoteChoice.Abstain] = "bg-secondary",

            [AssemblyVoteStatus.Draft] = "bg-secondary",
            [AssemblyVoteStatus.Open] = "bg-primary",
            [AssemblyVoteStatus.Closed] = "bg-success",
            [AssemblyVoteStatus.Cancelled] = "bg-danger",

            [AssemblyVoteVerdict.Passed] = "bg-success",
            [AssemblyVoteVerdict.Failed] = "bg-danger",
            [AssemblyVoteVerdict.Tie] = "bg-warning text-dark",

            [AssemblyBallotChoice.Yes] = "bg-success",
            [AssemblyBallotChoice.No] = "bg-danger",
            [AssemblyBallotChoice.Abstain] = "bg-secondary",
            [AssemblyBallotChoice.Ranked] = "bg-primary",
        });

        services.AddScoped<GovernanceEmails>();
        services.AddScoped<IEmailPreviewContributor, GovernanceEmailPreviews>();

        services.AddScoped<TermRenewalReminderJob>();
        services.AddScoped<MembershipTierExpiryJob>();

        services.AddSingleton<GovernanceMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<GovernanceMetricsService>());
    }

    string IIssueQueueOwner.QueueKey => "Governance";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Governance";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["board", "voting"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [RoleNames.Board];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Governance",
            SectionName = "Governance",
            Order = 40,
            Roles = ["Volunteer", "Board"],
            Features =
            [
                Of("View estatutos", ("Volunteer", Allowed), ("Board", Allowed)),
                Of("Apply for tier", ("Volunteer", Allowed), ("Board", Allowed)),
                Of("View applications", ("Volunteer", Limited), ("Board", Allowed)),
                Of("Vote on applications", ("Volunteer", Denied), ("Board", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<GovernanceBoardVotingController>("Voting", nameof(GovernanceBoardVotingController.BoardVoting), "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 pillCount: PillCounts.VotingQueueAsync),
            AdminLink<GovernanceApplicationsController>("Applications", nameof(GovernanceApplicationsController.Admin), "fa-solid fa-file-signature", PolicyNames.BoardOrAdmin),
            AdminLink<GovernanceVotesAdminController>("Assembly Votes", nameof(GovernanceVotesAdminController.Index), "fa-solid fa-landmark-flag", PolicyNames.BoardOrAdmin),
            // Temporary: term-expiry repair for rows approved under the pre-September-2026 rule.
            AdminLink<GovernanceApplicationsController>("Term expiry (temp)", nameof(GovernanceApplicationsController.AdminTermExpiry), "fa-solid fa-calendar-check", PolicyNames.AdminOnly)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(TierApplicationsCardViewComponent), Weight: 30),
            new(ChromeSlots.WidgetGallery, typeof(GovernanceGalleryViewComponent)),
        ];

    internal const string BodyLinksCopyKey = "governance.body-links-from-teams";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(BodyLinksCopyKey, "Governance",
            "Copy the Asociados, Colaboradores and Board teams' Google group address and Google files onto Governance's bodies",
            "The Google links of the three governance system teams, when those teams are deleted",
            BodyLinksCopyStatusAsync, ApplyBodyLinksCopyAsync),
    ];

    private static async Task<DataMigrationStatus> BodyLinksCopyStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Body", "From Teams (group / files)"],
            [.. (await sp.GetRequiredService<GovernanceBodyLinksService>().GetPendingCopiesAsync(ct))
                .Select(p => (IReadOnlyList<string>)[p.FromTeams.Body.ToString(), Describe(p.FromTeams)])]);

    private static string Describe(GovernanceBodyLinks links) =>
        $"{links.GoogleGroupPrefix ?? "no group"} / {string.Join(", ", links.Files.Select(f => $"{f.Name} ({f.PermissionLevel})"))}";

    private static Task<int> ApplyBodyLinksCopyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<GovernanceBodyLinksService>().CopyFromTeamsAsync(actorUserId, ct);

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Governance", 60),
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // 90-day horizon.
        yield return new RecurringJobDescriptor(
            "governance-term-renewal-reminder", typeof(TermRenewalReminderJob), "0 5 * * 1");

        // Tier term expiry. Five minutes after the hourly system-team sync's midnight run, which has
        // already dropped the lapsed members from their team; the sync reads the dated
        // approvals, so it never waits on this.
        yield return new RecurringJobDescriptor(
            "governance-membership-tier-expiry", typeof(MembershipTierExpiryJob), "5 0 * * *");

        // Close lapsed assembly votes and send the T-24h ballot reminder. Hourly is enough:
        // the service closes a lapsed vote inline on any request that sees the deadline, so
        // this only has to catch votes nobody looked at.
        yield return new RecurringJobDescriptor(
            "governance-assembly-vote-lapse", typeof(AssemblyVoteLapseJob), "0 * * * *");
    }

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MemberTermStatusViewComponent), Weight: 10),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(AssemblyVotesCardViewComponent), Weight: 15),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(GovernanceApplicationsTileViewComponent), Weight: 40),
    ];

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.GetUserId() is not { } userId)
        {
            return [];
        }

        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        return await VoteEntriesAsync(services, userId, localizer, ct);
    }

    /// <summary>
    /// One entry per open vote the member may cast in, done once they have a ballot on
    /// record. Non-roster members get nothing here: they can see the vote, but there is
    /// nothing for them to do.
    /// </summary>
    private static async ValueTask<IEnumerable<ThingsToDoEntry>> VoteEntriesAsync(
        IServiceProvider services, Guid userId, IStringLocalizer<SharedResource> localizer, CancellationToken ct)
    {
        var votes = await services.GetRequiredService<IAssemblyVoteService>()
            .GetVotesForMemberAsync(userId, ct);

        return votes
            .Where(v => v.Status == AssemblyVoteStatus.Open && v.IsOnRoster)
            .OrderByDescending(v => v.ClosesAt)
            .Select(v => new ThingsToDoEntry(
                $"assembly-vote-{v.Id}",
                localizer["Todo_AssemblyVote_Title"].Value,
                "fa-solid fa-check-to-slot",
                Controller: "GovernanceVotes", Action: "Index", Weight: 15)
            {
                Description = localizer[v.HasBallot ? "Todo_AssemblyVote_Done" : "Todo_AssemblyVote_Pending", v.Title],
                IsDone = v.HasBallot,
                ActionText = localizer["Todo_AssemblyVote_Action"].Value,
            })
            .ToList();
    }
}

internal static class PillCounts
{
    public static async ValueTask<int?> VotingQueueAsync(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>();
        var idClaim = http.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim.Value, out var userId))
            return null;
        var applications = sp.GetRequiredService<IGovernanceManagement>();
        var count = await applications.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }
}
