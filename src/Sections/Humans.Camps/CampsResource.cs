namespace Humans.Camps;

/// <summary>
/// The .resx files sit beside this file, so it must stay in <c>namespace Humans.Camps</c>: the manifest name comes
/// from this file's namespace, and any other makes every string fall back to its key. Public because the boot
/// localization check finds markers through <c>GetExportedTypes()</c>.
/// <para>
/// <c>Camp_Plural</c> stays in <c>SharedResource</c>: it also renders outside the section, which cannot see this set.
/// </para>
/// </summary>
public sealed class CampsResource;
