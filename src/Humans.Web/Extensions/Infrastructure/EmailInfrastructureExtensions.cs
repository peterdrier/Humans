using Humans.Email.Contracts;
using Humans.Base.Configuration;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// The <c>EmailSettings</c> binding stays in Shell because other sections read it (Auth's magic-link URL builder,
/// Profiles' unsubscribe tokens).
/// </summary>
internal static class EmailInfrastructureExtensions
{
    internal static IServiceCollection AddEmailInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.PostConfigure<EmailSettings>(settings =>
        {
            // Keep the transport and health probe aligned with the raw host setting.
            var smtpHost = configuration["Email:SmtpHost"];
            settings.SmtpHost = string.IsNullOrWhiteSpace(smtpHost) ? string.Empty : smtpHost;

            // Link builders append their own route separator.
            settings.BaseUrl = settings.BaseUrl.TrimEnd('/');

            if (settings.FromAddress.Contains("noreply", StringComparison.OrdinalIgnoreCase))
            {
                // Console.Error because ILogger isn't available during DI setup.
                Console.Error.WriteLine(
                    $"WARNING: Email:FromAddress is set to '{settings.FromAddress}'. " +
                    "System emails should come from 'humans@nobodies.team'. " +
                    "Check Coolify environment variable override.");
            }
        });

        // Registered in Shell: the section's OutboxEmailService depends on it, and Program.cs skips the Hangfire
        // stack in Testing, so ValidateOnBuild fails at startup without it.
        services.AddScoped<IImmediateOutboxProcessor, HangfireImmediateOutboxProcessor>();

        return services;
    }
}
