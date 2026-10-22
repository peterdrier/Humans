using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Grants.Authorization;
using Humans.Grants.Contracts;
using Humans.Grants.Data;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Grants;

public sealed class Section : ISection, ISectionAdminNav
{
    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Grants", [
            new("Programs", "GrantsAdmin", "Index", null, null,
                "fa-solid fa-gift", GrantsPolicies.Manage)
        ])
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<GrantsDbContext>(sentinelTable: "grant_programs");

        services.AddSingleton<IGrantRepository, GrantRepository>();

        services.AddScoped<GrantService>();
        services.AddScoped<IGrantManagement>(sp => sp.GetRequiredService<GrantService>());
        services.AddScoped<IGrantWrite>(sp => sp.GetRequiredService<GrantService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GrantService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GrantService>());

        services.AddScoped<IAuthorizationHandler, GrantsPolicyHandler>();
        services.AddScoped<IAuthorizationHandler, ProgramAuthorizationHandler>();

        services.AddScoped<GrantsEmails>();
        services.AddScoped<IEmailPreviewContributor, GrantsEmailPreviews>();

        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [GrantState.Active] = "bg-success",
            [GrantState.Expired] = "bg-secondary",
            [GrantState.Revoked] = "bg-danger",
            [GrantProgramStatus.Open] = "bg-success",
            [GrantProgramStatus.Closed] = "bg-secondary",
        });
    }
}
