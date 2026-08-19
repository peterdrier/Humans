using Humans.Email.Contracts;
using Humans.Base.Configuration;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// What is left in Shell after the Email section's G5 move: the <c>EmailSettings</c>
/// binding — read by Auth's magic-link URL builder, Profiles' unsubscribe token provider,
/// <c>SendReConsentReminderJob</c> and <c>SmtpHealthCheck</c>, so it is Base configuration
/// the section is merely named after — plus the startup guard that Production must have
/// SMTP configured. Everything else, including the two Hangfire job registrations that used
/// to live here, moved into <c>Humans.Email</c>'s <c>Section.Register</c> (#1074's jobs seam).
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
            if (settings.FromAddress.Contains("noreply", StringComparison.OrdinalIgnoreCase))
            {
                // Log at startup so operators notice the misconfiguration immediately.
                // This uses Console.Error because ILogger isn't available during DI setup.
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

        // The registration stays in Shell even though the implementation moved into the
        // section at G5 lane 5b-1: Program.cs treats the HangfireImmediateOutboxProcessor →
        // IImmediateOutboxProcessor → OutboxEmailService chain as Shell's (see the
        // Testing-environment Hangfire skip), and the section registers OutboxEmailService as
        // IEmailService taking this as a constructor dependency, so ValidateOnBuild fails at
        // startup without it.
        services.AddScoped<IImmediateOutboxProcessor, HangfireImmediateOutboxProcessor>();

        return services;
    }
}
