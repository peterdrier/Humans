using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using GovernanceApplicationDecisionService = Humans.Application.Services.Governance.ApplicationDecisionService;

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

        services.AddScoped<IOnboardingService, OnboardingService>();

        services.AddScoped<TermRenewalReminderJob>();

        return services;
    }
}
