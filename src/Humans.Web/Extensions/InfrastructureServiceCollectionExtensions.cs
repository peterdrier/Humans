using Humans.GoogleIntegration.Contracts;
using Humans.Agent.Contracts;
using Humans.Agent.Jobs;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Consent.Contracts;
using Humans.Consent.Jobs;
using Humans.Expenses.Contracts;
using Humans.Expenses.Jobs;
using Humans.Gate.Contracts;
using Humans.Gate.Jobs;
using Humans.Governance.Contracts;
using Humans.Governance.Jobs;
using Humans.Holded.Contracts;
using Humans.Holded.Jobs;
using Humans.Mailer.Contracts;
using Humans.Mailer.Jobs;
using Humans.Surveys.Contracts;
using Humans.Surveys.Jobs;
using Humans.Tickets.Contracts;
using Humans.Tickets.Jobs;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;
using Humans.Issues.Contracts;
using Humans.Issues.Jobs;
using Humans.Notifications.Contracts;
using Humans.Notifications.Jobs;
using Humans.Web.Extensions.Infrastructure;
using Humans.Web.Extensions.Sections;
using Humans.Users.Contracts;
using Humans.Web.Services.Dashboard;

using Humans.Teams.Contracts;

namespace Humans.Web.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddHumansInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ConfigurationRegistry? configRegistry = null)
    {
        // Cross-cutting infrastructure — options bindings, integrations, config metadata.
        services.AddConfigurationMetadata(configuration, configRegistry);
        services.AddTelemetryInfrastructure(configuration);
        services.AddEmailInfrastructure(configuration, environment);
        services.AddGoogleWorkspaceInfrastructure(configuration, environment);
        services.AddTicketVendorPort(configuration);
        // Stripe's own registrations moved with it to Humans.Stripe's Section.Register
        // (nobodies-collective/Humans#866, G5 lane 4b-2a).

        // Single key-addressed file storage rooted at wwwroot. Camps,
        // profile pictures, and any future file-bearing section share this
        // mount (production: Coolify volume at /app/wwwroot/uploads/).
        services.AddSingleton<IFileStorage, FileSystemFileStorage>();

        // Section-owned registrations. Each section file registers its own
        // repositories, services, jobs, options, and GDPR contributor forwarding.
        services.AddAuthSection();
        // AuditLog's read+render owner (IAuditViewerService) is registered by
        // Humans.AuditLog's own Section.Register since G5 lane 4b-2h
        // (nobodies-collective/Humans#866).
        services.AddAdminSection();

        // Recurring jobs for sections that have already moved out. The *registration* stays
        // here because UseHumansRecurringJobs names each job by concrete type and there is no
        // ISection-style discovery seam for jobs yet (design §15.6b) — but that never pinned
        // the job *type* to Humans.Infrastructure, and G5 lane 5b-1 re-measured the "Hangfire
        // serializes the declaring assembly" claim and found it false: AddOrUpdate<T>(id, …)
        // rewrites the stored type string at every startup, so the job id is the stable key.
        // Every job named below now lives in its own section's Jobs/ folder — G5 lane
        // 5b-5 emptied src/Humans.Infrastructure/Jobs/ (nobodies-collective/Humans#866).
        services.AddScoped<SendSurveyReminderJob>();
        services.AddScoped<GateRetentionJob>();
        services.AddScoped<GateVendorCheckInJob>();
        services.AddScoped<TicketSyncJob>();
        // TicketingBudgetSyncJob is registered by Humans.Budget's own Section.Register instead
        // (Peter's ruling 43 made ITicketingBudgetService internal, so only that assembly can
        // build the job's now-internal constructor).
        services.AddScoped<CleanupIssuesJob>();
        // Added at G5 lane 5b-1: "notifications-cleanup" is in UseHumansRecurringJobs' roll-call
        // but CleanupNotificationsJob had no DI registration anywhere, so Hangfire's
        // AspNetCoreJobActivator (GetRequiredService by concrete type) threw on every daily tick.
        services.AddScoped<CleanupNotificationsJob>();
        services.AddScoped<TermRenewalReminderJob>();
        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();
        services.AddTransient<MailerAudienceSyncJob>();
        // The two Holded-facing jobs are owned by different sections: HoldedSyncJob is Holded's
        // shim over IHoldedNightlySync, the expense-outbox drain is Expenses'. The connector
        // itself (client, options, call log) is registered by Humans.Holded's Section.cs since
        // G5 lane 4b-2f.
        services.AddScoped<HoldedSyncJob>();
        services.AddScoped<HoldedExpenseOutboxJob>();
        // Same miss as CleanupNotificationsJob above: the job was in the roll-call from the day
        // it shipped but was never registered, so the nightly purge threw instead of running and
        // no agent conversation has ever been deleted. A test now fails if a roll-call job has
        // no registration (Humans.Integration.Tests DiResolutionSmokeTests).
        services.AddScoped<AgentConversationRetentionJob>();

        // ActiveTeamsCacheInvalidator used to be registered here as a Base collaborator Teams'
        // section file registered on the way past. G5 lane 3a-1 re-measured that: its six
        // IMemoryCache-backed siblings moved to Base, but this one injects ITeamService and so
        // had to land in Humans.Teams — where HUM0034 makes it internal and therefore
        // unreachable from Web. Both the type and its registration are now Teams' (Section.cs).
        //
        // SystemTeamSyncJob left this list the same way at G5 lane 4b-2e — system-team
        // membership is a Teams invariant. Its interface followed at lane 5c and now lives on
        // Humans.Teams.Contracts.

        // Base collaborators that Governance's section file used to register on the way past.
        // The three badge-cache invalidators are Humans.Infrastructure implementations of
        // Humans.Application interfaces that four sections evict through — none of them is
        // Governance's to own (memory/architecture/governance-scope.md).
        // HumanLifecycleService left this list at G5 lane 4b-2d: it is Users' suspend/unsuspend
        // state machine and now registers from Humans.Users' Section.cs (Peter, 2026-08-14 —
        // Governance is governance only, never Users machinery).
        // Base's own nav-badge invalidator, sitting beside its siblings in Humans.Infrastructure.
        // Its registration lived in CampsSectionExtensions because Camps is the only consumer;
        // the section that owns the file is not always the section that owns the line
        // (design §15 step 4), and Section.Register may not register another layer's type.
        services.AddScoped<ICampLeadJoinRequestsBadgeCacheInvalidator, CampLeadJoinRequestsBadgeCacheInvalidator>();
        services.AddScoped<INavBadgeCacheInvalidator, NavBadgeCacheInvalidator>();
        services.AddScoped<INotificationMeterCacheInvalidator, NotificationMeterCacheInvalidator>();
        services.AddScoped<IVotingBadgeCacheInvalidator, VotingBadgeCacheInvalidator>();

        // Same call for Guide's section file: IGuideContentSource is a plain GitHub-markdown
        // fetcher (its signatures name only string) and three of its four consumers are not
        // Guide's — Humans.Agent's three preload readers, AgentDocsHealthCheck, and
        // GitHubCommunityKbContentSource — so the abstraction, the implementation and the
        // GuideSettings it binds all stay in Base.
        services.Configure<GuideSettings>(configuration.GetSection(GuideSettings.SectionName));
        services.AddSingleton<IGuideContentSource, GitHubGuideContentSource>();

        // Shell-resident collaborators of sections that have already moved out. AgentPreloadAugmentor
        // builds the access matrix, glossaries, route map and FAQ blocks of the agent's preload
        // corpus from the shared help registries (Humans.UI's AccessMatrixDefinitions and
        // SectionHelpContent); the section consumes it through the contracts leaf.
        services.AddSingleton<IAgentPreloadAugmentor, Humans.Web.Services.Agent.AgentPreloadAugmentor>();

        // AccountDeletionService, ExternalLoginService and UserParticipationBackfillService
        // left this list at G5 lane 5c (nobodies-collective/Humans#866): all three orchestrate
        // Users' own section and every outbound edge is another section's leaf, so they now
        // register from Humans.Users' Section.cs — the same call lane 4b-2d made for
        // HumanLifecycleService.

        // Dashboard's two aggregators. They were registered inside AddUsersSection and are
        // not Users' — they read every section's services and own no table — so they moved
        // here with the rest of the cluster at G5 lane 5c (Governance's rule: the section
        // that owns the file is not always the section that owns the line).
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();

        // Sections that have moved into their own project (nobodies-collective/Humans#866)
        // register themselves via ISection and are discovered, not named. The roll-call
        // above loses a line per section as the migration proceeds.
        services.AddDiscoveredSections(configuration);

        return services;
    }
}
