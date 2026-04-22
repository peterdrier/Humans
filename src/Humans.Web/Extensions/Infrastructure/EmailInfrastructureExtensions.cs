using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using EmailOutboxService = Humans.Application.Services.Email.EmailOutboxService;
using OutboxEmailService = Humans.Application.Services.Email.OutboxEmailService;
using InfrastructureEmailBodyComposer = Humans.Infrastructure.Services.BrandedEmailBodyComposer;

namespace Humans.Web.Extensions.Infrastructure;

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

        var hasSmtpConfig = !string.IsNullOrEmpty(configuration["Email:SmtpHost"]);

        if (hasSmtpConfig)
        {
            services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        }
        else if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Email SMTP configuration is required in production. Set Email:Host.");
        }
        else
        {
            services.AddScoped<IEmailTransport, StubEmailTransport>();
        }

        services.AddScoped<IEmailRenderer, EmailRenderer>();

        // Email section — §15 repository pattern (issue #548).
        // The outbox repository owns email_outbox_messages + the IsEmailSendingPaused
        // system_settings row. Registered Singleton (IDbContextFactory-based) so the
        // Application-layer services can inject it directly.
        services.AddSingleton<IEmailOutboxRepository, EmailOutboxRepository>();
        services.AddSingleton<IEmailBodyComposer, InfrastructureEmailBodyComposer>();
        services.AddScoped<IImmediateOutboxProcessor, HangfireImmediateOutboxProcessor>();
        services.AddScoped<IEmailService, OutboxEmailService>();
        services.AddScoped<IEmailOutboxService, EmailOutboxService>();

        services.AddScoped<ProcessEmailOutboxJob>();
        services.AddScoped<CleanupEmailOutboxJob>();

        return services;
    }
}
