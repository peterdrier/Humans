using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Jobs;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
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
public sealed class Section : ISection, IIssueQueueOwner
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

        // Assembly votes. The service is the embargo boundary, so nothing here exposes the
        // repository outside the section and no caching decorator wraps it — a cached tally
        // is a leaked tally.
        services.AddSingleton<IAssemblyVoteRepository, AssemblyVoteRepository>();
        services.AddScoped<AssemblyVoteService>();
        services.AddScoped<IAssemblyVoteService>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<AssemblyVoteService>());
        services.AddScoped<AssemblyVoteLapseJob>();

        // Query adapter breaks the circular DI graph between MembershipCalculator and
        // ITeamServiceRead / IRoleAssignmentService, both of which reach ISystemTeamSync —
        // RoleAssignmentService injects it, TeamService resolves it lazily through
        // IServiceProvider for this same reason — and whose implementation injects
        // IMembershipCalculatorRead back. Only MembershipCalculator depends on the adapter.
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

            [AssemblyVoteStatus.Draft] = "bg-secondary",
            [AssemblyVoteStatus.Open] = "bg-primary",
            [AssemblyVoteStatus.Closed] = "bg-success",
            [AssemblyVoteStatus.Cancelled] = "bg-danger",

            [AssemblyVoteVerdict.Passed] = "bg-success",
            [AssemblyVoteVerdict.Failed] = "bg-danger",
            [AssemblyVoteVerdict.Tie] = "bg-warning text-dark",

            [AssemblyBallotChoice.Yes] = "bg-success",
            [AssemblyBallotChoice.No] = "bg-danger",
            [AssemblyBallotChoice.Abstain] = "bg-secondary",
            [AssemblyBallotChoice.Ranked] = "bg-primary",
        });

        // Governance owns its email copy and its gallery samples; Email keeps the mechanics
        // (memory/architecture/email-templates-live-in-sender.md).
        services.AddScoped<GovernanceEmails>();
        services.AddScoped<IEmailPreviewContributor, GovernanceEmailPreviews>();

        services.AddScoped<TermRenewalReminderJob>();

        services.AddSingleton<GovernanceMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<GovernanceMetricsService>());
    }

    // This section owns the issue queue its members' reports land in; Issues discovers
    // the seam rather than holding a list of sections (memory/architecture/section-contribution-seams.md).
    string IIssueQueueOwner.QueueKey => "Governance";

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [RoleNames.Board];
}
