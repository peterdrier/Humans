using System.Net.Http.Headers;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Services.Mailer;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services.Mailer;
using Microsoft.Extensions.Options;

namespace Humans.Web.Extensions.Sections;

internal static class MailerSectionExtensions
{
    internal static IServiceCollection AddMailerSection(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Bind MailerLite options from "MailerLite:*" config section.
        // Second Configure pass applies a flat env-var fallback for PR/prod
        // environments that cannot use dotted key names (MAILERLITE_API_KEY).
        services
            .Configure<MailerLiteOptions>(configuration.GetSection(MailerLiteOptions.SectionName))
            .Configure<MailerLiteOptions>(opts =>
            {
                // Flat env-var fallback — PR/prod envs that can't use dotted keys.
                var flat = Environment.GetEnvironmentVariable("MAILERLITE_API_KEY");
                if (!string.IsNullOrWhiteSpace(flat) && string.IsNullOrWhiteSpace(opts.ApiKey))
                    opts.ApiKey = flat;
            });

        // Named HttpClient for MailerLite — resolved per-call by the Singleton
        // MailerLiteClient through IHttpClientFactory. Keeping the client
        // Singleton lets it cache subscribers/groups across requests.
        services.AddHttpClient(MailerLiteClient.HttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<MailerLiteOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.ApiKey);
            http.DefaultRequestHeaders.Add("X-Version", opts.ApiVersion);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddSingleton<IMailerLiteService, MailerLiteClient>();

        // Import orchestrator — stateless plan+apply, injected by the Admin controller.
        services.AddScoped<IMailerImportService, MailerImportService>();

        // Audience framework — orchestrator + audience registrations + recurring job.
        // Audiences are Scoped because their dependencies (ITicketQueryService,
        // IShiftView) are Scoped/decorated-Singleton.
        services.AddScoped<IMailerAudienceSyncService, MailerAudienceSyncService>();
        services.AddScoped<IMailerAudience, TicketNoShiftsAudience>();
        services.AddScoped<IMailerAudience, HasShiftAudience>();
        services.AddScoped<IMailerAudience, HasTicketAudience>();
        services.AddScoped<IMailerAudience, MarketingAudience>();
        services.AddScoped<IMailerAudience, MarketingNoTicketAudience>();
        services.AddTransient<MailerAudienceSyncJob>();

        return services;
    }
}
