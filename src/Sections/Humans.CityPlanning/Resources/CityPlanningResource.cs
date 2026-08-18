namespace Humans.CityPlanning;

/// <summary>
/// Marker type for City Planning's resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.CityPlanning</c> — <c>Humans.CityPlanning.Resources</c> would make
/// every City Planning string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence
/// (G5-SECTION-TEMPLATE.md step 3b). Shell's <c>Views/Camp/Details.cshtml</c> also injects
/// <c>IStringLocalizer&lt;CityPlanningResource&gt;</c> for the placement-phase strip.
/// </remarks>
public class CityPlanningResource;
