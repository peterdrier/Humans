using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Consent.Contracts;
using Humans.Issues.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Jobs;
using Humans.Consent.Services;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Consent;

public sealed class Section : ISection, IIssueQueueOwner, ISectionRoles
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
}
