using Humans.Application.Interfaces.Mailer;
using Humans.Application.Services.Mailer;
using Humans.Infrastructure.Services.Mailer;

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

        // Typed HttpClient — MailerLiteClient satisfies IMailerLiteService.
        // No Polly retry: Microsoft.Extensions.Http.Polly is not in this project;
        // MailerLiteClient handles rate-limit warnings internally (Task 21).
        services.AddHttpClient<IMailerLiteService, MailerLiteClient>();

        // Import orchestrator — stateless plan+apply, injected by the Admin controller.
        services.AddScoped<IMailerImportService, MailerImportService>();

        return services;
    }
}
