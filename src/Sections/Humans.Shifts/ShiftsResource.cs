namespace Humans.Shifts;

/// <summary>
/// Marker type for the Shifts resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Shifts</c> —
/// <c>Humans.Shifts.Resources</c> would make every string in the set fall back to its raw key
/// at runtime (design §3).
/// </summary>
/// <remarks>
/// <para>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15 step 3b).
/// </para>
/// <para>
/// The set holds every <c>ShiftDash_</c>, <c>VolTrack_</c>, <c>ShiftInfo_</c>, <c>EmailRota_</c>,
/// <c>EmailTeamRotas_</c>, <c>GetInvolved_</c>, <c>Dashboard_</c> and <c>DietaryMissingBanner_</c>
/// key and all but six <c>Shifts_</c> keys. One prefix and six keys stay in
/// <c>SharedResource</c>, each for a renderer that cannot see this set:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ShiftsSummary_</c> (7) — rendered by
/// <c>Humans.Teams/Views/Shared/_ShiftsSummaryCard.cshtml</c>: Teams builds the model and
/// renders it, so the keys are Teams' vocabulary and stay in <c>SharedResource</c> because
/// <c>Humans.Shifts</c> already references <c>Humans.Teams</c> and cannot be referenced back.
/// The partial binds <c>SharedLocalizer</c> for all seven.
/// </description></item>
/// <item><description>
/// <c>Shifts_SetUp</c> / <c>Shifts_Event</c> / <c>Shifts_Strike</c> — <c>Humans.Teams</c>'
/// roster period filter renders them, and <c>Humans.Shifts</c> already references
/// <c>Humans.Teams</c> (<c>ShiftAdminController : HumansTeamControllerBase</c>), so the
/// "consumer takes a reference and rebinds" direction is a cycle here.
/// <c>Shifts_AllPhases</c> joins them: it names the fourth <c>RotaPeriod</c> member in the
/// same switch expression in <c>_RotaBadges.cshtml</c>, and splitting one enum's display
/// names across two resource sets is what step 3b warns against.
/// </description></item>
/// <item><description>
/// <c>Shifts_All</c> / <c>Shifts_NoShiftsAvailable</c> — <c>Humans.Onboarding</c>'s shifts
/// step renders both, and <c>Humans.Shifts</c> references <c>Humans.Onboarding</c> for
/// <c>OnboardingResource</c>, so that is a cycle too.
/// </description></item>
/// </list>
/// <para>
/// Views read those six through <c>SharedLocalizer</c>, bound beside <c>Localizer</c> in
/// <c>Views/_ViewImports.cshtml</c>. Controllers resolve copy through
/// <c>IStringLocalizer&lt;ShiftsResource&gt;</c>, never <c>SharedResource</c>: controller-resolved
/// copy sits on the validation and error paths that no render test reaches.
/// </para>
/// <para>
/// <c>Humans.Users/Views/Profile/Edit.cshtml</c> is the one outside renderer that binds this
/// set: it injects <c>IStringLocalizer&lt;ShiftsResource&gt;</c> for the two shift-preference
/// keys rather than have them split off the section's own preferences card.
/// </para>
/// </remarks>
public class ShiftsResource;
