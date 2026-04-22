using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using ConsentConsentService = Humans.Application.Services.Consent.ConsentService;

namespace Humans.Web.Extensions.Sections;

internal static class LegalAndConsentSectionExtensions
{
    internal static IServiceCollection AddLegalAndConsentSection(this IServiceCollection services)
    {
        services.AddScoped<ILegalDocumentSyncService, LegalDocumentSyncService>();
        services.AddScoped<IAdminLegalDocumentService, AdminLegalDocumentService>();
        services.AddScoped<ILegalDocumentService, LegalDocumentService>();

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
