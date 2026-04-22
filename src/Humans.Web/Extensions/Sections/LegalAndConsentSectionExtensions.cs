using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class LegalAndConsentSectionExtensions
{
    internal static IServiceCollection AddLegalAndConsentSection(this IServiceCollection services)
    {
        services.AddScoped<ILegalDocumentSyncService, LegalDocumentSyncService>();
        services.AddScoped<IAdminLegalDocumentService, AdminLegalDocumentService>();
        services.AddScoped<ILegalDocumentService, LegalDocumentService>();

        services.AddScoped<ConsentService>();
        services.AddScoped<IConsentService>(sp => sp.GetRequiredService<ConsentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ConsentService>());

        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();

        return services;
    }
}
