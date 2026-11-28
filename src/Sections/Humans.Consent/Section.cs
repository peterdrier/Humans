using System.Security.Claims;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Base.Models.Tables;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Health;
using Humans.Consent.Jobs;
using Humans.Consent.Services;
using Humans.Consent.ViewComponents;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.Audit.Contracts;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;
using Humans.Consent.Controllers;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Consent;

public sealed class Section :
    SectionBase,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionHelp,
    ISectionJobs,
    ISectionHealthChecks,
    ISectionPolicies,
    IUserPart,
    ISectionChrome,
    ISectionMemberDashboard,
    ISectionThingsToDo
{
    void ISectionHealthChecks.AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<GitHubHealthCheck>("github", tags: [HealthCheckTags.External]);
    }

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(ConsentRoles.ConsentAdmin, "Consent Admin",
            "Guardians of trust and safety. They review every new human's consent journey, ensuring our community standards are understood and upheld from day one.",
            "fa-solid fa-shield-heart",
            FormerNames: ["ConsentCoordinator"]),
    ];

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [ConsentCheckStatus.Pending] = "bg-warning text-dark",
            [ConsentCheckStatus.Cleared] = "bg-success",
            [ConsentCheckStatus.Flagged] = "bg-danger",
        });

        services.AddSingleton<IAuditActionContributor, ConsentAuditActions>();
        services.AddSingleton<INotificationSourceContributor, ConsentNotificationSources>();
        services.AddScoped<INotificationMeterContributor, ConsentNotificationMeters>();
        services.AddSectionDbContext<LegalDbContext>(sentinelTable: "legal_documents");

        services.AddSingleton<ILegalDocumentRepository, LegalDocumentRepository>();
        services.AddScoped<IGitHubLegalDocumentConnector, GitHubLegalDocumentConnector>();

        // LegalDocumentSyncService is the sole writer and serves IAdminLegalDocumentService from the same keyed
        // inner instance, so admin and sync writes share one service.
        services.AddKeyedScoped<ILegalDocumentSyncManagement, LegalDocumentSyncService>(
            CachingLegalDocumentSyncService.InnerServiceKey);
        services.AddScoped<IAdminLegalDocumentService>(sp =>
            (LegalDocumentSyncService)sp.GetRequiredKeyedService<ILegalDocumentSyncManagement>(
                CachingLegalDocumentSyncService.InnerServiceKey));
        services.AddSingleton<CachingLegalDocumentSyncService>();
        services.AddSingleton<ILegalDocumentSyncManagement>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingLegalDocumentSyncService>());

        services.AddScoped<ILegalDocumentService, LegalDocumentService>();

        services.AddScoped<ILegalDocumentSyncRunner, LegalDocumentSyncRunner>();

        services.AddSingleton<IConsentRepository, ConsentRepository>();

        // The unkeyed concrete forwards to the keyed inner so IUserDataContributor reaches the undecorated service.
        services.AddKeyedScoped<IConsentManagement, ConsentService>(
            CachingConsentService.InnerServiceKey);
        services.AddScoped<ConsentService>(sp =>
            (ConsentService)sp.GetRequiredKeyedService<IConsentManagement>(
                CachingConsentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp =>
            sp.GetRequiredService<ConsentService>());

        services.AddSingleton<CachingConsentService>();
        services.AddSingleton<IConsentManagement>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentService>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentSubmission>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingConsentService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingConsentService>());

        services.AddSingleton<IConsentReviewRepository, ConsentReviewRepository>();
        services.AddScoped<ConsentReviewService>();
        services.AddScoped<IConsentWrite>(sp => sp.GetRequiredService<ConsentReviewService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ConsentReviewService>());
        services.AddScoped<IConsentReviewQueueService, ConsentReviewQueueService>();

        services.AddSingleton<IConsentReminderRepository, ConsentReminderRepository>();
        services.AddScoped<ConsentReminderService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ConsentReminderService>());

        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();

        services.AddScoped<ConsentEmails>();
        services.AddScoped<IEmailPreviewContributor, ConsentEmailPreviews>();

        services.AddSingleton<ConsentMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<ConsentMetricsService>());
    }

    // "Legal", not "Consent": stored issue rows still carry the section's old name.
    string IIssueQueueOwner.QueueKey => "Legal";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Legal";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["legal", "consent"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [ConsentRoles.ConsentAdmin];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "ConsentReview",
            SectionName = "Consent Review",
            Order = 50,
            Roles = ["ConsentAdmin", "Board"],
            Features =
            [
                Of("View review queue", ("ConsentAdmin", Allowed), ("Board", Allowed)),
                Of("Clear consent checks", ("ConsentAdmin", Allowed), ("Board", Allowed)),
                Of("Flag / reject signup", ("ConsentAdmin", Allowed), ("Board", Allowed)),
                Of("Board voting", ("ConsentAdmin", Denied), ("Board", Allowed)),
            ]
        },
        new()
        {
            Key = "LegalDocuments",
            SectionName = "Legal Documents",
            Order = 92,
            Roles = ["Board", "Admin"],
            Features =
            [
                Of("Legal documents", ("Board", Allowed), ("Admin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            AdminLink<ConsentReviewQueueController>("Review queue", nameof(ConsentReviewQueueController.Index), "fa-solid fa-clipboard-check", PolicyNames.ReviewQueueAccess,
                pillCount: ReviewQueuePillCountAsync),
            AdminLink<AdminLegalDocumentsController>("Legal documents", nameof(AdminLegalDocumentsController.LegalDocuments), "fa-solid fa-scale-balanced", PolicyNames.BoardOrAdmin)
        ])
    ];

    private static async ValueTask<int?> ReviewQueuePillCountAsync(IServiceProvider sp)
    {
        var users = sp.GetRequiredService<IUserService>();
        var reviews = await sp.GetRequiredService<IConsentService>().GetConsentReviewsAsync();
        var count = (await users.GetAllUserInfosAsync()).Count(u =>
            (reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None()).NeedsConsentReview(u));
        return count > 0 ? count : null;
    }

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => HelpEntries;

    private static readonly IReadOnlyList<SectionHelpEntry> HelpEntries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "ConsentReview", 70),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "LegalDocuments", 83),
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // Both at 04:00, 30 minutes before Users' SuspendNonCompliantMembersJob.
        yield return new RecurringJobDescriptor(
            "consent-reconsent-reminders", typeof(SendReConsentReminderJob), "0 4 * * *");

        yield return new RecurringJobDescriptor(
            "consent-legal-document-sync", typeof(SyncLegalDocumentsJob), "0 4 * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.ConsentAdminBoardOrAdmin, policy =>
            policy.RequireRole(ConsentRoles.ConsentAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(PolicyNames.ReviewQueueAccess, policy =>
            policy.RequireRole(ConsentRoles.ConsentAdmin, RoleNames.Board, RoleNames.Admin));
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(ConsentReviewCardViewComponent)),
        new(UserPartSlots.AccountStatus, typeof(RejectionReasonViewComponent)),
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(ConsentGalleryViewComponent))];

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
        [new(ChromeSlots.MemberDashboard, typeof(PendingConsentsAlertViewComponent), Weight: 5)];

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.GetUserId() is not { } userId)
        {
            return [];
        }

        var progress = await services.GetRequiredService<IConsentService>()
            .GetConsentProgressAsync(userId, ct);
        if (progress.RequiredCount == 0)
        {
            return [];
        }

        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var complete = progress.PendingCount == 0;
        return
        [
            new ThingsToDoEntry("consents", localizer["Todo_Consents_Title"].Value,
                "fa-solid fa-file-signature", Controller: "Consent", Action: "Index", Weight: 20)
            {
                Description = complete
                    ? localizer["Todo_Consents_Done"].Value
                    : localizer["Todo_Consents_Pending", progress.PendingCount, progress.RequiredCount],
                IsDone = complete,
                ActionText = localizer["Todo_Consents_Action"].Value,
            },
        ];
    }
}
