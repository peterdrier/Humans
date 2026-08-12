using Humans.Email.Contracts;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// What is left in Shell after the Email section's G5 move: the <c>EmailSettings</c>
/// binding — read by Auth's magic-link URL builder, Profiles' unsubscribe token provider,
/// <c>SendReConsentReminderJob</c> and <c>SmtpHealthCheck</c>, so it is Base configuration
/// the section is merely named after — plus the startup guard that Production must have
/// SMTP configured, the Hangfire-backed <see cref="IImmediateOutboxProcessor"/> and the two
/// recurring jobs (design §15 step 6b). Everything else moved into <c>Humans.Email</c>'s
/// <c>Section.Register</c>.
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

        // Stays in Shell rather than the section: the implementation is Hangfire-backed and
        // lives in Humans.Infrastructure, and Program.cs already treats the
        // HangfireImmediateOutboxProcessor → IImmediateOutboxProcessor → OutboxEmailService
        // chain as Shell's (see the Testing-environment Hangfire skip). The section registers
        // OutboxEmailService as IEmailService and takes this as a constructor dependency, so
        // ValidateOnBuild fails at startup without it.
        services.AddScoped<IImmediateOutboxProcessor, HangfireImmediateOutboxProcessor>();

        services.AddScoped<ProcessEmailOutboxJob>();
        services.AddScoped<CleanupEmailOutboxJob>();

        return services;
    }
}
