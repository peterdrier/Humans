using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Preapprovals.Authorization;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Jobs;
using Humans.Preapprovals.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Preapprovals;

public sealed class Section : ISection, ISectionAdminNav, ISectionJobs, ISectionPolicies
{
    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Pre-approvals", [
            new("Programs", "PreapprovalsAdmin", "Index", null, null,
                "fa-solid fa-stamp", PreapprovalsPolicies.Manage)
        ])
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<PreapprovalsDbContext>(sentinelTable: "pre_programs");

        services.AddSingleton<IPreapprovalRepository, PreapprovalRepository>();

        services.AddScoped<PreapprovalService>();
        services.AddScoped<IPreapprovalManagement>(sp => sp.GetRequiredService<PreapprovalService>());
        services.AddScoped<IPreapprovalService>(sp => sp.GetRequiredService<PreapprovalService>());
        services.AddScoped<IPreapprovalWrite>(sp => sp.GetRequiredService<PreapprovalService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<PreapprovalService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<PreapprovalService>());

        services.AddScoped<IAuthorizationHandler, PreapprovalsPolicyHandler>();
        services.AddScoped<IAuthorizationHandler, ProgramAuthorizationHandler>();

        services.AddScoped<PreapprovalRemindersJob>();

        services.AddScoped<PreapprovalsEmails>();
        services.AddScoped<IEmailPreviewContributor, PreapprovalsEmailPreviews>();

        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [GrantStatus.Applied] = "bg-warning text-dark",
            [GrantStatus.Open] = "bg-primary",
            [GrantStatus.Claimed] = "bg-info text-dark",
            [GrantStatus.Consumed] = "bg-success",
            [GrantStatus.Declined] = "bg-secondary",
            [GrantStatus.Revoked] = "bg-danger",
            [ProgramStatus.Open] = "bg-success",
            [ProgramStatus.Closed] = "bg-secondary",
        });
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            PreapprovalRemindersJob.JobId, typeof(PreapprovalRemindersJob), "0 5 * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PreapprovalsPolicies.Manage, policy =>
            policy.AddRequirements(new ManagesPreapprovalsRequirement()));
        options.AddPolicy(PreapprovalsPolicies.Access, policy =>
            policy.AddRequirements(new PreapprovalsAccessRequirement()));
    }
}
