using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Consent;
using Humans.Infrastructure.Repositories.Legal;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Consent;
using Humans.Infrastructure.Services.Legal;
using ConsentConsentService = Humans.Application.Services.Consent.ConsentService;
using LegalAdminLegalDocumentService = Humans.Application.Services.Legal.AdminLegalDocumentService;
using LegalLegalDocumentService = Humans.Application.Services.Legal.LegalDocumentService;
using LegalLegalDocumentSyncService = Humans.Application.Services.Legal.LegalDocumentSyncService;

namespace Humans.Web.Extensions.Sections;

internal static class LegalAndConsentSectionExtensions
{
    internal static IServiceCollection AddLegalAndConsentSection(this IServiceCollection services)
    {
        // Legal document section — §15 repository pattern (issue #547a).
        // ILegalDocumentRepository owns legal_documents + document_versions and
        // is shared across AdminLegalDocumentService and LegalDocumentSyncService.
        // GitHub I/O lives behind IGitHubLegalDocumentConnector in Infrastructure
        // so Application-side services stay free of Octokit.
        services.AddSingleton<ILegalDocumentRepository, LegalDocumentRepository>();
        services.AddScoped<IGitHubLegalDocumentConnector, GitHubLegalDocumentConnector>();

        // T-04: Inner LegalDocumentSyncService registered keyed under
        // CachingLegalDocumentSyncService.InnerServiceKey; unkeyed
        // ILegalDocumentSyncService resolves to the Singleton decorator.
        services.AddKeyedScoped<ILegalDocumentSyncService, LegalLegalDocumentSyncService>(
            CachingLegalDocumentSyncService.InnerServiceKey);
        services.AddSingleton<CachingLegalDocumentSyncService>();
        services.AddSingleton<ILegalDocumentSyncService>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ILegalDocumentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());

        // Post-#587: the TrackedCache base implements IHostedService and drives
        // WarmAllAsync at startup because the decorator's base ctor passes
        // warmOnStartup: true. Register the same Singleton instance as a
        // hosted service so the host calls StartAsync — no bespoke warmup
        // hosted service required.
        services.AddHostedService(sp => sp.GetRequiredService<CachingLegalDocumentSyncService>());

        // SaveChanges interceptor — fires the wholesale Legal-cache flush
        // whenever EF persists a write to legal_documents or document_versions.
        // Registered Singleton so the same instance is added to both AddDbContext
        // and AddDbContextFactory option pipelines.
        services.AddSingleton<LegalDocumentSaveChangesInterceptor>();

        services.AddScoped<IAdminLegalDocumentService, LegalAdminLegalDocumentService>();
        services.AddScoped<ILegalDocumentService, LegalLegalDocumentService>();

        // Legal & Consent section — ConsentService §15 repository pattern (issue #547).
        // consent_records is append-only per design-rules §12. IConsentRepository
        // is Singleton (IDbContextFactory-based).
        services.AddSingleton<IConsentRepository, ConsentRepository>();

        // T-04: Two-layer cache. Inner ConsentService registered keyed under
        // CachingConsentService.InnerServiceKey; unkeyed IConsentService
        // resolves to the Singleton decorator. SYNCHRONOUS invalidation on
        // SubmitConsentAsync is enforced inline in the decorator. Pattern
        // mirrors UsersSectionExtensions: keyed IConsentService registration
        // owns the lifecycle, unkeyed ConsentConsentService forwards to the
        // keyed registration via cast, and IUserDataContributor resolves the
        // unkeyed concrete type so the GdprExportDependencyInjectionTests'
        // factory replacement still applies cleanly.
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
        services.AddSingleton<IConsentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingConsentService>());

        // Post-#587: TrackedCache base implements IHostedService. Register the
        // Singleton instance as a hosted service for symmetry with the other
        // caching decorators; with warmOnStartup: false StartAsync is a no-op
        // and the cache stays lazy-per-key.
        services.AddHostedService(sp => sp.GetRequiredService<CachingConsentService>());

        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();

        return services;
    }
}
