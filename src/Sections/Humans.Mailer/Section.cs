using System.Net.Http.Headers;
using Humans.Base.Interfaces;
using Humans.Mailer.Contracts;
using Humans.Mailer.Services;
using Humans.Mailer.Services.Audiences;
using Humans.Mailer.Services.MailerLite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Mailer;

/// <summary>
/// Mailer's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// Mailer owns no tables, so there is no <c>AddSectionDbContext</c> call and no repository:
/// MailerLite is the system of record for subscriber state, and every Humans-side write
/// routes through another section's service (<c>IUserEmailService</c>,
/// <c>IAccountProvisioningService</c>, <c>ICommunicationPreferenceService</c>).
/// <para>
/// <c>MailerLiteOptions</c> binds here rather than in Shell — unlike Email's settings, which
/// four other sections read, <c>MailerLite:*</c> has exactly one consumer and it is the
/// section's own client. Shell keeps only the raw <c>MailerLite:AudienceSyncCron</c> read in
/// <c>RecurringJobExtensions</c>, which is the job schedule, not the client's configuration.
/// </para>
/// <para>
/// No caching decorator: <c>MailerLiteClient</c> is a Singleton that holds its own
/// subscriber/group snapshot and refreshes only on demand, which is what the §15 decorator
/// would otherwise be doing for a remote the section does not own.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
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
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.ApiKey);
            http.DefaultRequestHeaders.Add("X-Version", opts.ApiVersion);
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddSingleton<IMailerLiteService, MailerLiteClient>();

        // Import orchestrator — stateless plan+apply, injected by the Admin controller.
        services.AddScoped<IMailerImportService, MailerImportService>();

        // Audience framework — orchestrator + audience registrations.
        // Audiences are Scoped because their dependencies (ITicketServiceRead,
        // IShiftView) are Scoped/decorated-Singleton.
        services.AddScoped<MailerAudienceSyncService>();
        services.AddScoped<IMailerAudienceSyncService>(sp => sp.GetRequiredService<MailerAudienceSyncService>());
        services.AddScoped<IMailerAudienceSync>(sp => sp.GetRequiredService<MailerAudienceSyncService>());
        services.AddScoped<IMailerAudience, TicketNoShiftsAudience>();
        services.AddScoped<IMailerAudience, HasShiftAudience>();
        services.AddScoped<IMailerAudience, HasShiftSetupAudience>();
        services.AddScoped<IMailerAudience, HasShiftEventAudience>();
        services.AddScoped<IMailerAudience, HasShiftStrikeAudience>();
        services.AddScoped<IMailerAudience, HasTicketAudience>();
        services.AddScoped<IMailerAudience, MarketingAudience>();
        services.AddScoped<IMailerAudience, MarketingNoTicketAudience>();
    }
}
