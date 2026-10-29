using System.Globalization;
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
using Humans.Grants.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Grants;

public sealed class Section : ISection, ISectionAdminNav, ISectionPolicies, IUserPart, ISectionDataMigrations
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

    void ISectionPolicies.AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(GrantsPolicies.Manage, policy => policy.AddRequirements(new ManagesGrantsRequirement()));

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.Profile, typeof(MyGrantsViewComponent), Weight: 95),
        new(UserPartSlots.AdminDetail, typeof(UserGrantsViewComponent), Weight: 95),
    ];

    internal const string EventIdsKey = "grants.event-ids";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(EventIdsKey, "Grants",
            "Give every grant program the event of its year",
            "Programs without an event id, and the year fallback that reads them",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct)
    {
        var plan = await sp.GetRequiredService<IGrantManagement>().GetEventIdPlanAsync(ct);
        return new(["Program", "Name", "Year", "Event"],
            [
                .. plan.Select(p => (IReadOnlyList<string>)[
                    p.ProgramId.ToString(), p.ProgramName, p.Year.ToString(CultureInfo.InvariantCulture), p.Event.EventName]),
            ]);
    }

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IGrantManagement>().AssignProgramEventIdsAsync(ct);
}
