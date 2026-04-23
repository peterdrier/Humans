using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
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
        // No caching decorator — documents are a handful of rows and reads
        // don't dominate any hot path. GitHub I/O lives behind
        // IGitHubLegalDocumentConnector in Infrastructure so Application-side
        // services stay free of Octokit.
        services.AddSingleton<ILegalDocumentRepository, LegalDocumentRepository>();
        services.AddScoped<IGitHubLegalDocumentConnector, GitHubLegalDocumentConnector>();
        services.AddScoped<ILegalDocumentSyncService, LegalLegalDocumentSyncService>();
        services.AddScoped<IAdminLegalDocumentService, LegalAdminLegalDocumentService>();
        services.AddScoped<ILegalDocumentService, LegalLegalDocumentService>();

        // Legal & Consent section — ConsentService §15 repository pattern (issue #547).
        // consent_records is append-only per design-rules §12 — the repository
        // exposes only AddAsync + reads. No decorator: documents are few,
        // consent records grow with history but reads are per-user.
        // IConsentRepository is Singleton (IDbContextFactory-based) so the
        // service can inject it directly.
        services.AddSingleton<IConsentRepository, ConsentRepository>();
        services.AddScoped<ConsentConsentService>();
        services.AddScoped<IConsentService>(sp => sp.GetRequiredService<ConsentConsentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ConsentConsentService>());

        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();

        return services;
    }
}
