using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Governance;

/// <summary>
/// Governance's DI entry point. No caching decorator: at a handful of Board-driven writes a
/// week the service reads through the repository per request and invalidates the nav /
/// notification-meter / voting-badge caches inline after a write.
/// </summary>
public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionChrome,
    ISectionDataMigrations,
    ISectionHelp,
    ISectionJobs,
    ISectionMemberDashboard,
    ISectionNav,
    ISectionThingsToDo
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<GovernanceDbContext>(sentinelTable: "applications");

        services.AddSingleton<IApplicationRepository, ApplicationRepository>();

        services.AddSingleton<IMembershipTierRepository, MembershipTierRepository>();
        services.AddScoped<MembershipTierService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MembershipTierService>());
        // Snapshot legacy tier grants before ApplicationDecisionService moves their applications.
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<MembershipTierService>());

        services.AddScoped<ApplicationDecisionService>();
        services.AddScoped<IGovernanceManagement>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IGovernanceService>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ApplicationDecisionService>());

        services.AddScoped<IGovernanceIndexService, GovernanceIndexService>();

        // Assembly votes. The service is the embargo boundary, so nothing here exposes the
        // repository outside the section and no caching decorator wraps it — a cached tally
        // is a leaked tally.
        services.AddSingleton<IAssemblyVoteRepository, AssemblyVoteRepository>();
        services.AddScoped<AssemblyVoteService>();
        services.AddScoped<IAssemblyVoteService>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<AssemblyVoteLapseJob>();

        services.AddScoped<IMembershipQuery, MembershipQuery>();
        services.AddScoped<MembershipCalculator>();
        services.AddScoped<IMembershipCalculator>(sp => sp.GetRequiredService<MembershipCalculator>());

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
        },
        new()
        {
            Key = "Board",
            SectionName = "Board Dashboard",
            Order = 60,
            Roles = ["Board"],
            Features =
            [
                Of("Dashboard & stats", ("Board", Allowed)),
                Of("Audit log", ("Board", Allowed)),
            ]
        },
        new()
        {
            Key = "Admin",
            SectionName = "Admin Tools",
            Order = 90,
            Roles = ["Admin"],
            Features =
            [
                Of("Configuration status", ("Admin", Allowed)),
                Of("Sync settings", ("Admin", Allowed)),
                Of("Email outbox", ("Admin", Allowed)),
                Of("Background jobs", ("Admin", Allowed)),
                Of("All humans list", ("Admin", Allowed)),
                Of("Role assignments", ("Admin", Allowed)),
                Of("Legal documents", ("Admin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Governance", [
            new("Voting", "GovernanceBoardVoting", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueueAsync),
            new("Applications", "GovernanceApplications", "Admin", null, null, "fa-solid fa-file-signature", PolicyNames.BoardOrAdmin),
            new("Assembly Votes", "GovernanceVotesAdmin", "Index", null, null, "fa-solid fa-landmark-flag", PolicyNames.BoardOrAdmin),
            // Temporary: term-expiry repair for rows approved under the pre-September-2026 rule.
            new("Term expiry (temp)", "GovernanceApplications", "AdminTermExpiry", null, null, "fa-solid fa-calendar-check", PolicyNames.AdminOnly)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(TierApplicationsCardViewComponent), Weight: 30),
            new(ChromeSlots.WidgetGallery, typeof(GovernanceGalleryViewComponent)),
        ];

    internal const string ProfileTierKey = "governance.profile-tier";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(ProfileTierKey, "Governance",
            "Copy each Colaborador/Asociado tier from profiles.MembershipTier into governance_membership_tiers",
            "MembershipTierService's legacy fallback, ProfileInfo.MembershipTier and LegacyMembershipTier; then a drop PR for profiles.MembershipTier",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Tier", "Granted", "Term ends", "Granting application"],
            [.. (await sp.GetRequiredService<MembershipTierService>().GetUncopiedLegacyTiersAsync(ct))
                .Select(r => (IReadOnlyList<string>)[
                    r.UserId.ToString(),
                    r.Tier.ToString(),
                    r.GrantedAt.ToIso8601(),
                    r.ExpiresAt.ToInvariantDate() ?? string.Empty,
                    r.GrantedByApplicationId?.ToString() ?? string.Empty])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<MembershipTierService>().CopyLegacyTiersAsync(ct);

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Admin", 30),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Governance", 60),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Board", 80),
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // 90-day horizon.
        yield return new RecurringJobDescriptor(
            "governance-term-renewal-reminder", typeof(TermRenewalReminderJob), "0 5 * * 1");

        // Tier term expiry. Five minutes after the midnight UTC system-team sync, which has
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
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(PendingConsentsAlertViewComponent), Weight: 5),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MemberTermStatusViewComponent), Weight: 10),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(AssemblyVotesCardViewComponent), Weight: 15),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(GovernanceApplicationsTileViewComponent), Weight: 40),
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
    [
        new("Nav_Votes", Controller: "GovernanceVotes", Action: "Index",
            Policy: PolicyNames.AppAccess, Weight: 45)
    ];

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return [];
        }

        var snapshot = await services.GetRequiredService<IMembershipCalculator>()
            .GetMembershipSnapshotAsync(userId, ct);
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var complete = snapshot.PendingConsentCount == 0;

        List<ThingsToDoEntry> entries = [];

        if (snapshot.RequiredConsentCount > 0)
        {
            entries.Add(new ThingsToDoEntry("consents", localizer["Todo_Consents_Title"].Value,
                "fa-solid fa-file-signature", Controller: "Consent", Action: "Index", Weight: 20)
            {
                Description = complete
                    ? localizer["Todo_Consents_Done"].Value
                    : string.Format(CultureInfo.CurrentCulture, localizer["Todo_Consents_Pending"].Value,
                        snapshot.PendingConsentCount, snapshot.RequiredConsentCount),
                IsDone = complete,
                ActionText = localizer["Todo_Consents_Action"].Value,
            });
        }

        entries.AddRange(await VoteEntriesAsync(services, userId, localizer, ct));
        return entries;
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
            .Select(v => new ThingsToDoEntry(
                $"assembly-vote-{v.Id}",
                localizer["Todo_AssemblyVote_Title"].Value,
                "fa-solid fa-check-to-slot",
                Controller: "GovernanceVotes", Action: "Index", Weight: 15)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer[v.HasBallot ? "Todo_AssemblyVote_Done" : "Todo_AssemblyVote_Pending"].Value,
                    v.Title),
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
        var applications = sp.GetRequiredService<IGovernanceService>();
        var count = await applications.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }
}
