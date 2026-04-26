using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Governance;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Metering;
using GovernanceApplicationDecisionService = Humans.Application.Services.Governance.ApplicationDecisionService;
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

        // Metrics: Governance owns humans.applications_pending and humans.asociados.
        services.AddHostedService<GovernanceMetricsRegistrar>();

        return services;
    }
}
