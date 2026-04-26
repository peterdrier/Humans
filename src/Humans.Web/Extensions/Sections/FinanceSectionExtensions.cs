using Humans.Application.Configuration;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Finance;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class FinanceSectionExtensions
{
    internal static IServiceCollection AddFinanceSection(this IServiceCollection services, IConfiguration config)
    {
        // Bind HoldedSettings; ApiKey is overlaid from env var HOLDED_API_KEY
        // (kept out of appsettings so it never lands in source control).
        services.Configure<HoldedSettings>(s =>
        {
            config.GetSection(HoldedSettings.SectionName).Bind(s);
            var envKey = Environment.GetEnvironmentVariable("HOLDED_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey)) s.ApiKey = envKey;
        });

        // Repository — Singleton + IDbContextFactory per design-rules §15b.
        services.AddSingleton<IHoldedRepository, HoldedRepository>();

        // Application-layer services — scoped.
        services.AddScoped<IHoldedSyncService, HoldedSyncService>();
        services.AddScoped<IHoldedTransactionService, HoldedTransactionService>();

        // Vendor connector — typed HttpClient.
        services.AddHttpClient<IHoldedClient, HoldedClient>();

        // Hangfire job (registered as scoped so the recurring registration in
        // RecurringJobExtensions can resolve it).
        services.AddScoped<HoldedSyncJob>();

        return services;
    }
}
