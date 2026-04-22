using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using Humans.Web.Filters;
using GoogleSyncSettingsService = Humans.Application.Services.GoogleIntegration.SyncSettingsService;

namespace Humans.Web.Extensions.Sections;

internal static class AdminSectionExtensions
{
    internal static IServiceCollection AddAdminSection(this IServiceCollection services)
    {
        // Google Integration §15 migration (issue #554) — sync settings.
        // Repository is Singleton (IDbContextFactory-based); service is Scoped
        // and lives in Humans.Application.
        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddScoped<ISyncSettingsService, GoogleSyncSettingsService>();

        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();
        services.AddScoped<SendAdminDailyDigestJob>();
        services.AddScoped<SendBoardDailyDigestJob>();

        // Log API key (separate credential from feedback)
        services.Configure<LogApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("LOG_API_KEY") ?? string.Empty;
        });
        services.AddScoped<LogApiKeyAuthFilter>();

        return services;
    }
}
