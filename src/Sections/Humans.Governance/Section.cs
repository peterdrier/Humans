using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Jobs;
using Humans.Governance.Services;
using Humans.Base.Hosting;
using Humans.Base.Models.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Governance;

/// <summary>
/// Governance's DI entry point. No caching decorator: at a handful of Board-driven writes a
/// week the service reads through the repository per request and invalidates the nav /
/// notification-meter / voting-badge caches inline after a write.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<GovernanceDbContext>(sentinelTable: "applications");

        services.AddSingleton<IApplicationRepository, ApplicationRepository>();

        services.AddScoped<ApplicationDecisionService>();
        services.AddScoped<IApplicationDecisionService>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IApplicationServiceRead>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ApplicationDecisionService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<ApplicationDecisionService>());

        services.AddScoped<IGovernanceIndexService, GovernanceIndexService>();

        // Query adapter breaks the circular DI graph between MembershipCalculator
        // and ITeamServiceRead / IRoleAssignmentService (both of which inject
        // ISystemTeamSync, whose implementation injects IMembershipCalculatorRead back).
        // Only MembershipCalculator depends on the query adapter.
        services.AddScoped<IMembershipQuery, MembershipQuery>();
        services.AddScoped<MembershipCalculator>();
        services.AddScoped<IMembershipCalculatorRead>(sp => sp.GetRequiredService<MembershipCalculator>());

        // Base cannot name a moved section's enums, so the section pushes its badge rows into
        // the Humans.UI registry rather than Humans.UI referencing this section
        // (memory/architecture/base-ui-registries-are-section-populated.md).
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [ApplicationStatus.Submitted] = "bg-primary",
            [ApplicationStatus.Approved] = "bg-success",
            [ApplicationStatus.Rejected] = "bg-danger",

            [VoteChoice.Yay] = "bg-success",
            [VoteChoice.Maybe] = "bg-warning text-dark",
            [VoteChoice.No] = "bg-danger",
            [VoteChoice.Abstain] = "bg-secondary",
        });

        services.AddScoped<TermRenewalReminderJob>();

        services.AddSingleton<GovernanceMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<GovernanceMetricsService>());
    }
}
