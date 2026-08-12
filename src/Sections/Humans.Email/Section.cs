using Humans.Application.Interfaces;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Services;
using Humans.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email;

/// <summary>
/// Email's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. No caching decorator: the outbox is a
/// sequential queue drain, not a hot-path read shape.
/// </summary>
/// <remarks>
/// <c>EmailSettings</c> itself is bound in Shell, not here: Auth's magic-link URL builder,
/// Profiles' unsubscribe token provider, <c>SendReConsentReminderJob</c> and Shell's
/// <c>SmtpHealthCheck</c> all read it, so it is Base configuration this section happens to
/// be named after (Governance's rule — the section that owns the file is not always the
/// section that owns the line). The transport *choice* is Email's and lives here.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<EmailDbContext>(sentinelTable: "email_outbox_messages");

        // Singleton + IDbContextFactory so the repository can be injected into the
        // Scoped services and the outbox drain alike (§15 repository pattern).
        services.AddSingleton<IEmailOutboxRepository, EmailOutboxRepository>();

        // SmtpEmailTransport when Email:SmtpHost is configured, the logging stub
        // otherwise. Shell keeps the "required in Production" guard, where IHostEnvironment
        // is available at startup rather than at first resolve.
        if (!string.IsNullOrEmpty(configuration["Email:SmtpHost"]))
        {
            services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        }
        else
        {
            services.AddScoped<IEmailTransport, StubEmailTransport>();
        }

        services.AddScoped<IEmailRenderer, EmailRenderer>();
        services.AddSingleton<IEmailBodyComposer, BrandedEmailBodyComposer>();
        services.AddScoped<IEmailMessageFactory, EmailMessageFactory>();
        services.AddScoped<IEmailService, OutboxEmailService>();

        services.AddScoped<EmailOutboxService>();
        services.AddScoped<IEmailOutboxService>(sp => sp.GetRequiredService<EmailOutboxService>());
        services.AddScoped<IEmailOutboxServiceRead>(sp => sp.GetRequiredService<EmailOutboxService>());
        services.AddScoped<IEmailOutboxRetention>(sp => sp.GetRequiredService<EmailOutboxService>());

        services.AddScoped<IEmailOutboxProcessor, EmailOutboxProcessor>();
    }
}
