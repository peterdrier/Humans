using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Governance;
using Humans.Infrastructure.Services;
using GovernanceApplicationDecisionService = Humans.Application.Services.Governance.ApplicationDecisionService;
using GovernanceBoardVotingMeterContributor = Humans.Application.Services.Governance.BoardVotingMeterContributor;
using OnboardingOrchestratorService = Humans.Application.Services.Onboarding.OnboardingService;

namespace Humans.Web.Extensions.Sections;

internal static class GovernanceSectionExtensions
{
    internal static IServiceCollection AddGovernanceSection(this IServiceCollection services)
    {
        // Governance — repository + service, no caching decorator.
        // Governance is low-traffic enough that DB reads per request are fine;
        // the service invalidates nav/notification/voting badge caches inline
        // after successful writes.
        services.AddScoped<IApplicationRepository, ApplicationRepository>();

        services.AddScoped<INavBadgeCacheInvalidator, NavBadgeCacheInvalidator>();
        services.AddScoped<INotificationMeterCacheInvalidator, NotificationMeterCacheInvalidator>();
        services.AddScoped<IVotingBadgeCacheInvalidator, VotingBadgeCacheInvalidator>();

        services.AddScoped<GovernanceApplicationDecisionService>();
        services.AddScoped<IApplicationDecisionService>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GovernanceApplicationDecisionService>());

        // Onboarding — orchestrator only (owns no tables). Lives in Humans.Application
        // per design-rules §2b; routes all reads/writes through owning-section
        // service interfaces (IProfileService, IUserService, IApplicationDecisionService,
        // ISystemTeamSync, etc.). Takes no DbContext dependency.
        services.AddScoped<OnboardingOrchestratorService>();
        services.AddScoped<IOnboardingService>(sp => sp.GetRequiredService<OnboardingOrchestratorService>());
        // Narrow interface that breaks the DI cycle with ProfileService / ConsentService.
        services.AddScoped<IOnboardingEligibilityQuery>(sp => sp.GetRequiredService<OnboardingOrchestratorService>());

        services.AddScoped<TermRenewalReminderJob>();

        // Notification meter contributor owned by this section (push-model per
        // issue nobodies-collective/Humans#581). Per-user scope; self-caches
        // under CacheKeys.VotingBadge(userId) which is also read by
        // NavBadgesViewComponent so cache warmth is shared.
        services.AddScoped<INotificationMeterContributor, GovernanceBoardVotingMeterContributor>();

        return services;
    }
}
