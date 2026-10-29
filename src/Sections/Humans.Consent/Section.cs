using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
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

namespace Humans.Consent;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAdminNav,
    ISectionDataMigrations,
    ISectionJobs,
    ISectionPolicies,
    IUserPart
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(ConsentRoles.ConsentCoordinator, "Consent Coordinator",
            "Guardians of trust and safety. They review every new human's consent journey, ensuring our community standards are understood and upheld from day one.",
            "fa-solid fa-shield-heart"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, ConsentNotificationSources>();
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
        services.AddSingleton<ILegalDocumentSyncService>(sp =>
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

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [ConsentRoles.ConsentCoordinator];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Consent", [
            new("Legal documents", "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.BoardOrAdmin)
        ])
    ];

    internal const string ProfileReviewKey = "consent.profile-review";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(ProfileReviewKey, "Consent",
            "Copy each Consent Coordinator review (consent check, approval, rejection) from profiles into consent_reviews, and record a legacy rejection as users.State = Rejected where the state never did",
            "ConsentReviewService's legacy fallback, UserInfo.IsRejected's legacy arm, the review fields on ProfileInfo and LegacyConsentCheckStatus; then a drop PR for the profiles review columns",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Copy row", "Set Rejected state", "Status", "Approved", "Checked", "Rejected"],
            [.. (await sp.GetRequiredService<ConsentReviewService>().GetUncopiedLegacyReviewsAsync(ct))
                .Select(p => (IReadOnlyList<string>)[
                    p.Review.UserId.ToString(),
                    p.CopyRow ? "yes" : "no",
                    p.SetRejectedState ? "yes" : "no",
                    p.Review.Status?.ToString() ?? string.Empty,
                    p.Review.IsApproved ? "yes" : "no",
                    p.Review.CheckedAt.ToIso8601() ?? string.Empty,
                    p.Review.RejectedAt.ToIso8601() ?? string.Empty])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<ConsentReviewService>().CopyLegacyReviewsAsync(ct);

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // 04:00, 30 minutes before Users' SuspendNonCompliantMembersJob.
        yield return new RecurringJobDescriptor(
            "consent-reconsent-reminders", typeof(SendReConsentReminderJob), "0 4 * * *");

        yield return new RecurringJobDescriptor(
            "consent-legal-document-sync", typeof(SyncLegalDocumentsJob), "0 4 * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.ConsentCoordinatorBoardOrAdmin, policy =>
            policy.RequireRole(ConsentRoles.ConsentCoordinator, RoleNames.Board, RoleNames.Admin));
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(ConsentReviewCardViewComponent)),
        new(UserPartSlots.AccountStatus, typeof(RejectionReasonViewComponent)),
    ];
}
