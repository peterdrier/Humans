using Humans.Email.Contracts;
using Humans.Base.Configuration;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// The <c>EmailSettings</c> binding stays in Shell because other sections read it (Auth's magic-link URL builder,
/// Profiles' unsubscribe tokens), plus the startup guard that Production must have SMTP configured.
/// </summary>
internal static class EmailInfrastructureExtensions
{
    internal static IServiceCollection AddEmailInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));
        services.PostConfigure<EmailSettings>(settings =>
        {
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

        // The section binds the transport off the same key; this is the startup half of
        // that decision, kept here because IHostEnvironment is not a Section.Register
        // argument and a deferred throw would first fire on a real send.
        if (string.IsNullOrEmpty(configuration["Email:SmtpHost"]) && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Email SMTP configuration is required in production. Set Email:Host.");
        }

        // Registered in Shell: the section's OutboxEmailService depends on it, and Program.cs skips the Hangfire
        // stack in Testing, so ValidateOnBuild fails at startup without it.
        services.AddScoped<IImmediateOutboxProcessor, HangfireImmediateOutboxProcessor>();

        return services;
    }
}
