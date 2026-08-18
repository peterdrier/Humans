namespace Humans.Camps.Resources;

/// <summary>
/// Marker for the Camps section's resource set (design §3). Must stay <c>public</c> — the boot
/// diagnostic's <c>SectionResourceTypes()</c> reads <c>GetExportedTypes()</c> and skips an
/// internal marker in silence. The <c>.cs</c> and the <c>.resx</c> files must sit in the same
/// folder: this type's namespace determines the manifest prefix.
///
/// <para>
/// <c>Camp_Plural</c> stays in <c>SharedResource</c> — Shell's nav and dashboard, the guest
/// landing page, Containers' breadcrumb and Events' barrio form all render it, and a key
/// rendered outside the section cannot see the section's set (design §15 step 3b, carve by
/// renderer). The section binds <c>SharedLocalizer</c> for it and for the <c>Common_*</c>,
/// <c>CityPlanning_*</c> and Base-enum keys its views still read.
/// </para>
/// </summary>
public sealed class CampsResource;
