using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Cantina.Contracts;
using Humans.Cantina.Data;
using Humans.Cantina.Services;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Cantina;

/// <summary>
/// Cantina's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// Two lanes: the members' dietary profiles (<c>cantina_dietary_profiles</c>, its own context,
/// repository and service, which also answers export/erase and account merge) and the roster
/// service, which composes over <c>IShiftService</c>, <c>ISettingsService</c>,
/// <c>IUserService</c> and the dietary profiles. Access is the <c>CantinaAdminOrAdmin</c> policy,
/// registered by this section's own <see cref="SectionPolicies"/> via <c>ISectionPolicies</c> —
/// not in the Shell. (The G5 split design's §8 parked policy registration in Shell's
/// <c>AuthorizationPolicyExtensions</c>; <c>ISectionPolicies</c> superseded that, and the
/// Shell's own header says so.)
/// <para>
/// The line moved here out of <c>ShiftsSectionExtensions.AddShiftsSection</c>, where it had
/// been parked because the on-site cohort comes from Shifts: the section that owns the
/// <em>file</em> is not always the section that owns the <em>line</em>
/// (G5-SECTION-TEMPLATE.md step 4).
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CantinaDbContext>(sentinelTable: "cantina_dietary_profiles");
        services.AddScoped<IDietaryProfileRepository, DietaryProfileRepository>();
        services.AddScoped<DietaryProfileService>();
        services.AddScoped<IDietaryProfileManagement>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<ICantinaService>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<DietaryProfileService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<DietaryProfileService>());

        // Read-only service that stitches the on-site cohort + dietary breakdown for the
        // /Cantina/Roster and /Cantina/Roster/Day pages (feature #36 —
        // src/Sections/Humans.Cantina/Docs/features/daily-roster.md).
        services.AddScoped<ICantinaRosterService, CantinaRosterService>();
    }
}
