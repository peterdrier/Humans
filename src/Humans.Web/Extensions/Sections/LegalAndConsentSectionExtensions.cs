using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Consent;
using Humans.Infrastructure.Repositories.Legal;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Consent;
using Humans.Infrastructure.Services.Legal;
using ConsentConsentService = Humans.Application.Services.Consent.ConsentService;
using LegalLegalDocumentService = Humans.Application.Services.Legal.LegalDocumentService;
using LegalLegalDocumentSyncService = Humans.Application.Services.Legal.LegalDocumentSyncService;

namespace Humans.Web.Extensions.Sections;

internal static class LegalAndConsentSectionExtensions
{
    internal static IServiceCollection AddLegalAndConsentSection(this IServiceCollection services)
    {
        // Legal documents — see #547a. GitHub I/O behind IGitHubLegalDocumentConnector keeps Application free of Octokit.
        services.AddSingleton<ILegalDocumentRepository, LegalDocumentRepository>();
        services.AddScoped<IGitHubLegalDocumentConnector, GitHubLegalDocumentConnector>();

        // Keyed-Scoped inner + Singleton decorator. LegalDocumentSyncService is the sole writer for
        // legal_documents/document_versions (nobodies-collective/Humans#751) and implements both
        // ILegalDocumentSyncService and IAdminLegalDocumentService; IAdminLegalDocumentService forwards
        // to the same keyed inner instance so admin writes and GitHub-sync writes share one service.
        services.AddKeyedScoped<ILegalDocumentSyncService, LegalLegalDocumentSyncService>(
            CachingLegalDocumentSyncService.InnerServiceKey);
        services.AddScoped<IAdminLegalDocumentService>(sp =>
            (LegalLegalDocumentSyncService)sp.GetRequiredKeyedService<ILegalDocumentSyncService>(
                CachingLegalDocumentSyncService.InnerServiceKey));
        services.AddSingleton<CachingLegalDocumentSyncService>();
        services.AddSingleton<ILegalDocumentSyncService>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ILegalDocumentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());

        // TrackedCache StartAsync drives WarmAllAsync (warmOnStartup: true) — see #587.
        services.AddHostedService(sp => sp.GetRequiredService<CachingLegalDocumentSyncService>());

        services.AddScoped<ILegalDocumentService, LegalLegalDocumentService>();

        // ConsentService — see #547. consent_records is append-only (design-rules §12).
        services.AddSingleton<IConsentRepository, ConsentRepository>();

        // Two-layer cache: keyed inner + Singleton decorator. Unkeyed concrete forwards via cast so IUserDataContributor resolves the inner cleanly for GDPR-export tests.
        services.AddKeyedScoped<IConsentService, ConsentConsentService>(
            CachingConsentService.InnerServiceKey);
        services.AddScoped<ConsentConsentService>(sp =>
            (ConsentConsentService)sp.GetRequiredKeyedService<IConsentService>(
                CachingConsentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp =>
            sp.GetRequiredService<ConsentConsentService>());

        services.AddSingleton<CachingConsentService>();
        services.AddSingleton<IConsentService>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentServiceRead>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingConsentService>());

        // Hosted for symmetry; warmOnStartup: false so StartAsync is a no-op.
        services.AddHostedService(sp => sp.GetRequiredService<CachingConsentService>());

        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();

        return services;
    }
}
