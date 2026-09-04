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
/// The set is the <c>Email_*</c> subject/body keys. They are *not* the Email admin
/// pages' copy — those two views carry no localized string at all — they are every
/// section's transactional email text, and they came here because
/// <c>EmailRenderer</c>, their one and only renderer, is now a section type (the G5
/// playbook, step 3b's carve-by-renderer, applied in the direction that moves the renderer).
/// </remarks>
public class EmailResource;
