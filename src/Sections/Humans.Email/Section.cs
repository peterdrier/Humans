using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Data;
using Humans.Email.Health;
using Humans.Email.Jobs;
using Humans.Email.Services;
using Humans.Email.ViewComponents;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.AuditLog.Contracts;

namespace Humans.Email;

/// <summary><c>EmailSettings</c> is bound in Shell, not here: many sections read it.</summary>
public sealed class Section :
    ISection,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionChrome,
    ISectionConfiguration,
    ISectionDataMigrations,
    ISectionHealthChecks,
    ISectionJobs,
    ISectionSettings,
    IUserPart
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, EmailAuditActions>();
        services.AddSectionDbContext<EmailDbContext>(sentinelTable: "email_outbox_messages");

        services.AddSingleton<IEmailOutboxRepository, EmailOutboxRepository>();

        // Missing SMTP must not block boot or silently discard production mail through the
        // stub. Resolve the environment here; Section.Register has no environment argument.
        var hasSmtpHost = !string.IsNullOrWhiteSpace(configuration["Email:SmtpHost"]);
        services.AddScoped<IEmailTransport>(sp =>
            hasSmtpHost || sp.GetRequiredService<IHostEnvironment>().IsProduction()
                ? ActivatorUtilities.CreateInstance<SmtpEmailTransport>(sp)
                : ActivatorUtilities.CreateInstance<StubEmailTransport>(sp));

        services.AddSingleton<IEmailBodyComposer, BrandedEmailBodyComposer>();
        services.AddSingleton<EmailPreviewService>();
        services.AddSingleton<IEmailPreviewService>(sp => sp.GetRequiredService<EmailPreviewService>());
        services.AddSingleton<IEmailPreviewManagement>(sp => sp.GetRequiredService<EmailPreviewService>());
        services.AddScoped<IEmailMessageFactory, EmailMessageFactory>();
        services.AddScoped<IEmailPreviewContributor, FacilitatedMessagePreviews>();
        services.AddScoped<IEmailService, OutboxEmailService>();
        services.AddScoped<IImmediateOutboxProcessor, HangfireImmediateOutboxProcessor>();
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

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Email", [
            new("Preview", "Email", "EmailPreview", null, null, "fa-solid fa-envelope", PolicyNames.AdminOnly, Weight: 0),
            new("Outbox",  "Email", "EmailOutbox",  null, null, "fa-solid fa-inbox",    PolicyNames.AdminOnly, Weight: 10)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("email.outbox", "Emails", "fa-solid fa-envelope", OutboxAsync, Weight: 110)
    ];

    private static async ValueTask<AdminTileValue?> OutboxAsync(IServiceProvider sp, CancellationToken ct)
    {
        // recentMessageCount: 0 — the tile wants the count, not the messages.
        var stats = await sp.GetRequiredService<IEmailOutboxManagement>().GetOutboxStatsAsync(0, ct);
        return new AdminTileValue(stats.TotalCount.ToString("N0", CultureInfo.CurrentCulture), Detail: "outbox total");
    }

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [
            new(ChromeSlots.WidgetGallery, typeof(EmailGalleryViewComponent)),
        ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetRequiredSetting(registry, "Email:SmtpHost", "Email");
        configuration.GetOptionalSetting(registry, "Email:Username", "Email", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Email:Password", "Email", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetRequiredSetting(registry, "Email:FromAddress", "Email");
        configuration.GetRequiredSetting(registry, "Email:BaseUrl", "Email");
        configuration.GetOptionalSetting(registry, "Email:DpoAddress", "Email",
            importance: ConfigurationImportance.Recommended);
    }

    internal const string CommunicationPreferencesKey = "email.communication-preferences";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(CommunicationPreferencesKey, "Email",
            "Copy every communication-preference row from Users' communication_preferences into email_communication_preferences",
            "CommunicationPreferenceService's legacy fallback, its cleared rows, UserInfo.CommunicationPreferences and LegacyMessageCategory; then a drop PR for communication_preferences",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Category", "Opted out", "Inbox", "Source", "Updated at", "Subscribed at"],
            [.. (await sp.GetRequiredService<ICommunicationPreferenceManagement>().GetUncopiedLegacyPreferencesAsync(ct))
                .Select(u => (IReadOnlyList<string>)
                [
                    u.UserId.ToString(), u.Preference.Category.ToString(),
                    u.Preference.OptedOut ? "yes" : "no", u.Preference.InboxEnabled ? "on" : "off",
                    u.Preference.UpdateSource,
                    u.Preference.UpdatedAt.ToIso8601(),
                    u.Preference.SubscribedAt.ToIso8601() ?? string.Empty,
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<ICommunicationPreferenceManagement>().CopyLegacyPreferencesAsync(ct);

    void ISectionHealthChecks.AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<SmtpHealthCheck>("smtp", tags: [HealthCheckTags.External]);
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "email-outbox-process", typeof(ProcessEmailOutboxJob), "*/1 * * * *");

        yield return new RecurringJobDescriptor(
            "email-outbox-cleanup", typeof(CleanupEmailOutboxJob), "0 3 * * 0");
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("email", "Settings_TabEmail", typeof(EmailPauseSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.AdminDetail, typeof(OutboxCardViewComponent), Weight: 200),
        new(UserPartSlots.AdminDetail, typeof(CommunicationPreferencesCardViewComponent), Weight: 210),
    ];
}
