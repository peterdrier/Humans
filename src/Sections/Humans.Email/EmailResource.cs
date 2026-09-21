namespace Humans.Email;

/// <summary>
/// Marker type for Email's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Email</c> — <c>Humans.Email.Resources</c> would make every
/// transactional email body fall back to its raw key at runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence.
/// The set is the three <c>Email_FacilitatedMessage_*</c> keys. They are *not* the Email
/// admin pages' copy — those two views carry no localized string at all — they are the copy
/// of the one template this section still renders, the person-to-person relay. Every other
/// section's transactional text moved into that section's own resx set with its
/// <c>&lt;Section&gt;Emails</c> builder (peterdrier/Humans#1651).
/// </remarks>
public class EmailResource;
