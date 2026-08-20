using Humans.Base.Interfaces;
using Humans.Cantina.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Cantina;

/// <summary>
/// Cantina's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// One registration: the roster service. Cantina owns no tables, so there is no
/// <c>AddSectionDbContext</c> call and no repository — the service composes over
/// <c>IShiftManagementServiceRead</c>, <c>IBurnSettingsService</c> and <c>IUserServiceRead</c>,
/// each registered by its own owner. Access is the <c>CantinaAdminOrAdmin</c> policy, which
/// stays in Shell's <c>AuthorizationPolicyExtensions</c> (design §8).
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
        // Read-only service that stitches the on-site cohort + dietary breakdown for the
        // /Cantina/Roster and /Cantina/Roster/Day pages (feature #36 —
        // src/Sections/Humans.Cantina/Docs/features/daily-roster.md).
        services.AddScoped<ICantinaRosterService, CantinaRosterService>();
    }
}
