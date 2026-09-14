namespace Humans.Users;

/// <summary>
/// Marker type for the Users resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Users</c> —
/// <c>Humans.Users.Resources</c> would make every string in the set fall back to its raw key
/// at runtime (design §3).
/// </summary>
/// <remarks>
/// <para>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15 step 3b).
/// </para>
/// <para>
/// Views read the section's keys through <c>Localizer</c>; leftovers that stayed in
/// <c>SharedResource</c> (shared vocabulary, or read by another section's renderer) go
/// through <c>SharedLocalizer</c>, bound beside it in <c>Views/_ViewImports.cshtml</c>.
/// <c>UserArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet</c> stops
/// a controller quietly keeping <c>IStringLocalizer&lt;SharedResource&gt;</c> for a key
/// that belongs here.
/// </para>
/// <para>
/// <c>Views/Profile/Edit.cshtml</c> keeps <c>IStringLocalizer&lt;ShiftsResource&gt;</c> for
/// the two shift-preference labels on its preferences card: the card is Users' own markup
/// over Users' own form, the copy is Shifts' vocabulary, and the
/// <c>Humans.Users</c> → <c>Humans.Shifts</c> reference that binding needs is required
/// anyway by <c>&lt;vc:shift-signups&gt;</c> on <c>Profile/Index</c> and
/// <c>UsersAdmin/AdminDetail</c>.
/// </para>
/// </remarks>
public class UsersResource;
