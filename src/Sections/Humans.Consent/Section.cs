using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Jobs;
using Humans.Consent.Services;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Consent;

/// <summary>
/// Consent's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <c>SyncLegalDocumentsJob</c> and <c>SendReConsentReminderJob</c> moved into this project's
/// <c>Contracts/</c> folder (nobodies-collective/Humans#866): the former at G5 lane 5b-4, the
/// latter at lane 5b-5. Their registration followed at #1074's jobs seam, whose schedule is
/// contributed via <c>SectionJobs.cs</c>.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<LegalDbContext>(sentinelTable: "legal_documents");

        // Legal documents — see #547a. GitHub I/O behind IGitHubLegalDocumentConnector keeps the
        // section's services free of Octokit.
        services.AddSingleton<ILegalDocumentRepository, LegalDocumentRepository>();
        services.AddScoped<IGitHubLegalDocumentConnector, GitHubLegalDocumentConnector>();

        // Keyed-Scoped inner + Singleton decorator. LegalDocumentSyncService is the sole writer for
        // legal_documents/document_versions (nobodies-collective/Humans#751) and implements both
        // ILegalDocumentSyncService and IAdminLegalDocumentService; IAdminLegalDocumentService forwards
        // to the same keyed inner instance so admin writes and GitHub-sync writes share one service.
        services.AddKeyedScoped<ILegalDocumentSyncService, LegalDocumentSyncService>(
            CachingLegalDocumentSyncService.InnerServiceKey);
        services.AddScoped<IAdminLegalDocumentService>(sp =>
            (LegalDocumentSyncService)sp.GetRequiredKeyedService<ILegalDocumentSyncService>(
                CachingLegalDocumentSyncService.InnerServiceKey));
        services.AddSingleton<CachingLegalDocumentSyncService>();
        services.AddSingleton<ILegalDocumentSyncService>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ILegalDocumentSyncServiceRead>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ILegalDocumentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingLegalDocumentSyncService>());

        // TrackedCache StartAsync drives WarmAllAsync (warmOnStartup: true) — see #587.
        services.AddHostedService(sp => sp.GetRequiredService<CachingLegalDocumentSyncService>());

        services.AddScoped<ILegalDocumentService, LegalDocumentService>();

        // The GitHub sync + re-consent fan-out the recurring job used to run itself, back
        // inside the section where the entity and the repository live (design §15 step 6b).
        services.AddScoped<ILegalDocumentSyncRunner, LegalDocumentSyncRunner>();

        // ConsentService — see #547. consent_records is append-only (design-rules §12).
        services.AddSingleton<IConsentRepository, ConsentRepository>();

        // Two-layer cache: keyed inner + Singleton decorator. Unkeyed concrete forwards via cast so IUserDataContributor resolves the inner cleanly for GDPR-export tests.
        services.AddKeyedScoped<IConsentService, ConsentService>(
            CachingConsentService.InnerServiceKey);
        services.AddScoped<ConsentService>(sp =>
            (ConsentService)sp.GetRequiredKeyedService<IConsentService>(
                CachingConsentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp =>
            sp.GetRequiredService<ConsentService>());

        services.AddSingleton<CachingConsentService>();
        services.AddSingleton<IConsentService>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentServiceRead>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentSubmission>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingConsentService>());

        // Hosted for symmetry; warmOnStartup: false so StartAsync is a no-op.
        services.AddHostedService(sp => sp.GetRequiredService<CachingConsentService>());

        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();
    }
}
