using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.HumanLifecycle;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Governance;
using Humans.Infrastructure.Services;
using Humans.Web.Services.Onboarding;
using GovernanceApplicationDecisionService = Humans.Application.Services.Governance.ApplicationDecisionService;
using GovernanceMembershipCalculator = Humans.Application.Services.Governance.MembershipCalculator;
using GovernanceMembershipQuery = Humans.Application.Services.Governance.MembershipQuery;
using OnboardingOrchestratorService = Humans.Application.Services.Onboarding.OnboardingService;
using OnboardingWidgetStateService = Humans.Application.Services.Onboarding.OnboardingWidgetState;

namespace Humans.Web.Extensions.Sections;

internal static class GovernanceSectionExtensions
{
    internal static IServiceCollection AddGovernanceSection(this IServiceCollection services)
    {
        // Governance — repository + service, no caching decorator.
        // Governance is low-traffic enough that DB reads per request are fine;
        // the service invalidates nav/notification/voting badge caches inline
        // after successful writes.
        services.AddSingleton<IApplicationRepository, ApplicationRepository>();

        services.AddScoped<INavBadgeCacheInvalidator, NavBadgeCacheInvalidator>();
        services.AddScoped<INotificationMeterCacheInvalidator, NotificationMeterCacheInvalidator>();
        services.AddScoped<IVotingBadgeCacheInvalidator, VotingBadgeCacheInvalidator>();

        services.AddScoped<GovernanceApplicationDecisionService>();
        services.AddScoped<IApplicationDecisionService>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());

        // Query adapter breaks the circular DI graph between IMembershipCalculator
        // and ITeamService / IRoleAssignmentService (both of which inject
        // ISystemTeamSync, whose implementation injects IMembershipCalculator back).
        // Only MembershipCalculator depends on the query adapter.
        services.AddScoped<IMembershipQuery, GovernanceMembershipQuery>();
        services.AddScoped<IMembershipCalculator, GovernanceMembershipCalculator>();

        // Onboarding — orchestrator only (owns no tables). Lives in Humans.Application
        // per design-rules §2b; routes all reads/writes through owning-section
        // service interfaces (IProfileService, IUserService, IApplicationDecisionService,
        // ISystemTeamSync, etc.). Takes no DbContext dependency.
        services.AddScoped<OnboardingOrchestratorService>();
        services.AddScoped<IOnboardingService>(sp => sp.GetRequiredService<OnboardingOrchestratorService>());
        // Narrow interface that breaks the DI cycle with ProfileService / ConsentService.
        services.AddScoped<IOnboardingEligibilityQuery>(sp => sp.GetRequiredService<OnboardingOrchestratorService>());

        services.AddScoped<IOnboardingWidgetState, OnboardingWidgetStateService>();
        services.AddScoped<IOnboardingWidgetSessionState, HttpOnboardingWidgetSessionState>();

        // Human lifecycle — state-machine on already-onboarded humans
        // (suspend / unsuspend; future re-consent suspensions, term-renewal,
        // status recompute). Owns no tables — orchestrates IProfileService +
        // notification dispatch. Extracted from OnboardingService in
        // nobodies-collective#583 (umbrella nobodies-collective#563).
        services.AddScoped<IHumanLifecycleService, HumanLifecycleService>();

        services.AddScoped<TermRenewalReminderJob>();

        return services;
    }
}
