using Humans.GoogleIntegration.Contracts;
using Humans.Agent.Contracts;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Services.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.HumanLifecycle;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Extensions.Infrastructure;
using Humans.Web.Extensions.Sections;
using Humans.Users.Contracts;

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
        services.AddStripeInfrastructure(configuration);

        // Single key-addressed file storage rooted at wwwroot. Camps,
        // profile pictures, and any future file-bearing section share this
        // mount (production: Coolify volume at /app/wwwroot/uploads/).
        services.AddSingleton<IFileStorage, FileSystemFileStorage>();

        // Section-owned registrations. Each section file registers its own
        // repositories, services, jobs, options, and GDPR contributor forwarding.
        services.AddProfileSection(configuration);
        services.AddUsersSection();
        services.AddAuthSection();
        services.AddEarlyEntrySection();
        // AuditLog's read+render owner. It resolves actor/subject/team display names
        // through IUserServiceRead, ITeamServiceRead and ITeamResourceService, which makes
        // it a cross-section orchestrator rather than part of the horizontal AuditLog
        // section (peters-hard-rules.md: a horizontal may not reference a vertical), so it
        // stays in Humans.Application and is registered here — Governance's rule, that the
        // section owning the file is not always the section owning the line.
        services.AddScoped<IAuditViewerService, AuditViewerService>();
        services.AddICalFeedSection();
        services.AddAdminSection();
        services.AddHoldedConnector(configuration);

        // Recurring jobs for sections that have already moved out. The job types stay in
        // Humans.Infrastructure/Jobs because UseHumansRecurringJobs names them by concrete
        // type and there is no ISection-style discovery seam for jobs yet (design §15.6b);
        // each reaches its section through that section's contracts leaf.
        services.AddScoped<SendSurveyReminderJob>();
        services.AddScoped<GateRetentionJob>();
        services.AddScoped<GateVendorCheckInJob>();
        services.AddScoped<TicketSyncJob>();
        services.AddScoped<TicketingBudgetSyncJob>();
        services.AddScoped<CleanupIssuesJob>();
        services.AddScoped<TermRenewalReminderJob>();
        services.AddScoped<SyncLegalDocumentsJob>();
        services.AddScoped<SendReConsentReminderJob>();
        services.AddTransient<MailerAudienceSyncJob>();

        // Base collaborators that Teams' section file used to register on the way past.
        // ActiveTeamsCacheInvalidator is a Humans.Infrastructure implementation of a
        // Humans.Application interface (the IInvalidator family other sections evict Teams'
        // master cache entry through), and SystemTeamSyncJob is a Humans.Infrastructure job
        // bound to GoogleIntegration's ISystemTeamSync — neither is Teams' to own
        // (design §15 step 4, Governance's rule: the section that owns the file is not always
        // the section that owns the line).
        services.AddScoped<IActiveTeamsCacheInvalidator, ActiveTeamsCacheInvalidator>();
        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        // Base collaborators that Governance's section file used to register on the way past.
        // The three badge-cache invalidators are Humans.Infrastructure implementations of
        // Humans.Application interfaces that four sections evict through, and
        // HumanLifecycleService is the suspend/unsuspend state machine over IProfileService —
        // none of them is Governance's to own (memory/architecture/governance-scope.md).
        // Base's own nav-badge invalidator, sitting beside its siblings in Humans.Infrastructure.
        // Its registration lived in CampsSectionExtensions because Camps is the only consumer;
        // the section that owns the file is not always the section that owns the line
        // (design §15 step 4), and Section.Register may not register another layer's type.
        services.AddScoped<ICampLeadJoinRequestsBadgeCacheInvalidator, CampLeadJoinRequestsBadgeCacheInvalidator>();
        services.AddScoped<INavBadgeCacheInvalidator, NavBadgeCacheInvalidator>();
        services.AddScoped<INotificationMeterCacheInvalidator, NotificationMeterCacheInvalidator>();
        services.AddScoped<IVotingBadgeCacheInvalidator, VotingBadgeCacheInvalidator>();
        services.AddScoped<IHumanLifecycleService, HumanLifecycleService>();

        // Same call for Guide's section file: IGuideContentSource is a plain GitHub-markdown
        // fetcher (its signatures name only string) and three of its four consumers are not
        // Guide's — Humans.Agent's three preload readers, AgentDocsHealthCheck, and
        // GitHubCommunityKbContentSource — so the abstraction, the implementation and the
        // GuideSettings it binds all stay in Base.
        services.Configure<GuideSettings>(configuration.GetSection(GuideSettings.SectionName));
        services.AddSingleton<IGuideContentSource, GitHubGuideContentSource>();

        // Shell-resident collaborators of sections that have already moved out. AgentPreloadAugmentor
        // builds the access matrix, glossaries, route map and FAQ blocks of the agent's preload
        // corpus from Shell-owned help content (AccessMatrixDefinitions, SectionHelpContent), so it
        // cannot move into Humans.Agent; the section consumes it through the contracts leaf.
        services.AddSingleton<IAgentPreloadAugmentor, Humans.Web.Services.Agent.AgentPreloadAugmentor>();

        // Users' CSV participation backfill. Its registration sat in the Tickets section file
        // because /Tickets/ParticipationBackfill is the only page that drives it, but the
        // service is Humans.Application.Services.Users' and reads only IUserService /
        // IShiftManagementService — the section that owns the file is not always the section
        // that owns the line (memory/architecture/governance-scope.md's rule, Governance
        // finding 94).
        services.AddScoped<IUserParticipationBackfillService, UserParticipationBackfillService>();

        // Sections that have moved into their own project (nobodies-collective/Humans#866)
        // register themselves via ISection and are discovered, not named. The roll-call
        // above loses a line per section as the migration proceeds.
        services.AddDiscoveredSections(configuration);

        return services;
    }
}
