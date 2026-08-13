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
/// The set is 328 keys: all of <c>ShiftDash_</c> (99), <c>VolTrack_</c> (94),
/// <c>ShiftInfo_</c> (13), <c>EmailRota_</c> (11), <c>EmailTeamRotas_</c> (9), and 102 of the
/// 108 <c>Shifts_</c>. Two prefixes and six keys stayed in <c>SharedResource</c>, each for a
/// renderer that cannot see this set:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ShiftsSummary_</c> (7) — rendered by <c>Humans.UI/Views/Shared/_ShiftsSummaryCard.cshtml</c>.
/// <c>Humans.UI</c> cannot reference a section, so a key it renders stays shared (Teams' rule).
/// The partial and its view model are logged as deferred debt: they are Shifts vocabulary and
/// belong here eventually, and the keys follow them when they move.
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
/// <c>Views/_ViewImports.cshtml</c>. <c>ShiftsArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet</c>
/// is what stops a controller quietly keeping <c>IStringLocalizer&lt;SharedResource&gt;</c>
/// after its keys moved here — a failure mode no render test reaches, because
/// controller-resolved copy sits on the validation and error paths (Surveys' finding).
/// </para>
/// <para>
/// Shell's <c>Views/Profile/Edit.cshtml</c> is the one outside renderer that <em>could</em>
/// rebind and did: it injects <c>IStringLocalizer&lt;ShiftsResource&gt;</c> for the two
/// shift-preference keys rather than have them split off the section's own preferences card.
/// </para>
/// </remarks>
public class ShiftsResource { }
