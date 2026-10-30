using System.Security.Claims;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Cantina.Contracts;
using Humans.Cantina.Data;
using Humans.Cantina.Services;
using Humans.Gdpr.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Cantina;

public sealed class Section :
    ISection,
    ISectionRoles,
    ISectionAdminNav,
    ISectionPolicies,
    ISectionThingsToDo
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(CantinaRoles.CantinaAdmin, "Cantina Admin",
            "Meal planners. They read the weekly roster, the dietary breakdown and the allergy rollups so the kitchen cooks for the people who are actually on site.",
            "fa-solid fa-utensils"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CantinaDbContext>(sentinelTable: "cantina_dietary_profiles");
        services.AddScoped<IDietaryProfileRepository, DietaryProfileRepository>();
        services.AddScoped<DietaryProfileService>();
        services.AddScoped<IDietaryProfileManagement>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<ICantinaService>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<DietaryProfileService>());

        services.AddScoped<ICantinaRosterService, CantinaRosterService>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Cantina", [
            new("Roster", "Cantina", "Roster", null, null, "fa-solid fa-utensils", PolicyNames.CantinaAdminOrAdmin)
        ])
    ];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.CantinaAdminOrAdmin, policy =>
            policy.RequireRole(CantinaRoles.CantinaAdmin, RoleNames.Admin));
    }

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return [];

        var profile = await services.GetRequiredService<ICantinaService>().GetDietaryProfileAsync(userId, ct);
        if (profile is { HasPreference: true } or { DeclinedToAnswer: true })
            return [];

        // A failure here is logged by ThingsToDoViewComponent, which drops this contributor's entries.
        var hasQualifyingSignup = await services.GetRequiredService<IShiftService>()
            .HasQualifyingCantinaSignupAsync(userId, ct);
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        return
        [
            new ThingsToDoEntry("dietary", localizer["Todo_DietaryMedical_Title"].Value,
                "fa-solid fa-utensils", Controller: "CantinaProfile", Action: "Dietary", Weight: 50)
            {
                Description = localizer[hasQualifyingSignup
                    ? "Todo_DietaryMedical_Pending"
                    : "Todo_DietaryMedical_NoShift_Pending"].Value,
                ActionText = localizer["Todo_DietaryMedical_Action"].Value,
            },
        ];
    }
}
