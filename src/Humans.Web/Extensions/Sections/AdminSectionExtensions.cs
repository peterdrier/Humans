using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Filters;

namespace Humans.Web.Extensions.Sections;

internal static class AdminSectionExtensions
{
    internal static IServiceCollection AddAdminSection(this IServiceCollection services)
    {
        services.AddScoped<ISyncSettingsService, SyncSettingsService>();

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
