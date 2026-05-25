using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Admin;
using Humans.Infrastructure.Services;
using Humans.Web.Filters;

namespace Humans.Web.Extensions.Sections;

internal static class AdminSectionExtensions
{
    internal static IServiceCollection AddAdminSection(this IServiceCollection services)
    {
        services.AddScoped<ProcessAccountDeletionsJob>();
        services.AddScoped<SuspendNonCompliantMembersJob>();
        services.AddSingleton<IAdminDatabaseDiagnosticsRepository, AdminDatabaseDiagnosticsRepository>();
        services.AddScoped<IAdminDatabaseDiagnosticsService, AdminDatabaseDiagnosticsService>();

        // Log API key (separate credential from feedback)
        services.Configure<LogApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("LOG_API_KEY") ?? string.Empty;
        });
        services.AddScoped<LogApiKeyAuthFilter>();

        return services;
    }
}
