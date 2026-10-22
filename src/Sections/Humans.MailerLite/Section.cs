using System.Net.Http.Headers;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.MailerLite.Data;
using Humans.MailerLite.Jobs;
using Humans.MailerLite.Services;
using Humans.MailerLite.Services.Audiences;
using Humans.MailerLite.Services.MailerLite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.MailerLite;

/// <summary>
/// <c>MailerLiteOptions</c> binds here, not in Shell: the section's own client is its only consumer. No caching
/// decorator: the Singleton client holds its own snapshot and refreshes on demand.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // The section's only table, so its presence proves the baseline ran.
        services.AddSectionDbContext<MailerLiteDbContext>(sentinelTable: "mailerlite_sync_states");
        services.AddScoped<IMailerLiteRepository, Repository>();

        services
            .Configure<MailerLiteOptions>(configuration.GetSection(MailerLiteOptions.SectionName))
            .Configure<MailerLiteOptions>(opts =>
            {
                // Flat env var for hosts that can't set dotted keys.
                var flat = Environment.GetEnvironmentVariable("MAILERLITE_API_KEY");
                if (!string.IsNullOrWhiteSpace(flat) && string.IsNullOrWhiteSpace(opts.ApiKey))
                    opts.ApiKey = flat;
            });

        // The client is a Singleton so it can cache subscribers and groups across requests.
        services.AddHttpClient(MailerLiteClient.HttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<MailerLiteOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.ApiKey);
            http.DefaultRequestHeaders.Add("X-Version", opts.ApiVersion);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddSingleton<IMailerLiteService, MailerLiteClient>();

        // No user-scoped tables, but Article 17 erasure must still delete the MailerLite subscriber.
        services.AddScoped<MailerLiteGdprContributor>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<MailerLiteGdprContributor>());

        services.AddScoped<IMailerLiteImportService, MailerLiteImportService>();

        // Audiences are Scoped because their dependencies are.
        services.AddScoped<IMailerLiteAudienceSyncService, MailerLiteAudienceSyncService>();
        services.AddScoped<IMailerLiteAudience, TicketNoShiftsAudience>();
        services.AddScoped<IMailerLiteAudience, HasShiftAudience>();
        services.AddScoped<IMailerLiteAudience, HasShiftSetupAudience>();
        services.AddScoped<IMailerLiteAudience, HasShiftEventAudience>();
        services.AddScoped<IMailerLiteAudience, HasShiftStrikeAudience>();
        services.AddScoped<IMailerLiteAudience, HasTicketAudience>();
        services.AddScoped<IMailerLiteAudience, MarketingAudience>();
        services.AddScoped<IMailerLiteAudience, MarketingNoTicketAudience>();

        services.AddTransient<MailerLiteAudienceSyncJob>();
    }
}
