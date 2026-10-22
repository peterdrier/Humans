using System.Security.Claims;
using System.Threading.RateLimiting;
using Humans.Base.Interfaces;
using Humans.Email.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Email.Data;
using Humans.Email.Jobs;
using Humans.Email.Services;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email;

/// <summary><c>EmailSettings</c> is bound in Shell, not here: many sections read it.</summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<EmailDbContext>(sentinelTable: "email_outbox_messages");

        services.AddSingleton<IEmailOutboxRepository, EmailOutboxRepository>();

        // Without an SMTP host the logging stub sends. Shell enforces "required in Production", where the
        // environment is known at startup.
        if (!string.IsNullOrEmpty(configuration["Email:SmtpHost"]))
        {
            services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        }
        else
        {
            services.AddScoped<IEmailTransport, StubEmailTransport>();
        }

        services.AddSingleton<IEmailBodyComposer, BrandedEmailBodyComposer>();
        services.AddSingleton<EmailPreviewService>();
        services.AddSingleton<IEmailPreviewService>(sp => sp.GetRequiredService<EmailPreviewService>());
        services.AddSingleton<IEmailPreviewManagement>(sp => sp.GetRequiredService<EmailPreviewService>());
        services.AddScoped<IEmailMessageFactory, EmailMessageFactory>();
        services.AddScoped<IEmailPreviewContributor, FacilitatedMessagePreviews>();
        services.AddScoped<IEmailService, OutboxEmailService>();
        services.AddScoped<ComposerSelfSendService>();

        // The composer's "Send to me" is an open, authenticated send: cap it per member on top of the
        // Shell's global limiter.
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(
            EmailPreviewController.SelfSendRateLimitPolicy,
            context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = EmailPreviewController.SelfSendPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true,
                })));

        services.AddScoped<EmailOutboxService>();
        services.AddScoped<IEmailOutboxManagement>(sp => sp.GetRequiredService<EmailOutboxService>());
        services.AddScoped<IEmailOutboxService>(sp => sp.GetRequiredService<EmailOutboxService>());
        services.AddScoped<IEmailOutboxRetention>(sp => sp.GetRequiredService<EmailOutboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<EmailOutboxService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<EmailOutboxService>());

        services.AddSingleton<ICommunicationPreferenceRepository, CommunicationPreferenceRepository>();
        services.AddScoped<IUnsubscribeTokenProvider, UnsubscribeTokenProvider>();
        services.AddScoped<CommunicationPreferenceService>();
        services.AddScoped<ICommunicationPreferenceManagement>(sp => sp.GetRequiredService<CommunicationPreferenceService>());
        services.AddScoped<ICommunicationPreferenceService>(sp => sp.GetRequiredService<CommunicationPreferenceService>());
        services.AddScoped<ICommunicationPreferenceWrite>(sp => sp.GetRequiredService<CommunicationPreferenceService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CommunicationPreferenceService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CommunicationPreferenceService>());
        services.AddScoped<IUnsubscribeService, UnsubscribeService>();

        services.AddScoped<IEmailOutboxProcessor, EmailOutboxProcessor>();

        services.AddScoped<ProcessEmailOutboxJob>();
        services.AddScoped<CleanupEmailOutboxJob>();
    }
}
