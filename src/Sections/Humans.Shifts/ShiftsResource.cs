namespace Humans.Shifts;

/// <summary>
/// The .resx files sit beside this file, so it must stay in <c>namespace Humans.Shifts</c>: the manifest name comes
/// from this file's namespace, and any other makes every string fall back to its key. Public because the boot
/// localization check finds markers through <c>GetExportedTypes()</c>.
/// </summary>
/// <remarks>
/// Keys rendered by Teams or Onboarding (<c>ShiftsSummary_</c>, <c>Shifts_SetUp</c>, <c>Shifts_All</c>…) stay in
/// SharedResource: Shifts references those projects, so they cannot bind this set. Controllers never use SharedResource.
/// </remarks>
public class ShiftsResource;
