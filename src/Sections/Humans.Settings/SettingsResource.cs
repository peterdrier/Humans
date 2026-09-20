namespace Humans.Settings;

/// <summary>
/// Marker for the Settings section's resource set (design §3). Gained member-facing strings
/// when <c>/Settings</c> shipped (peterdrier/Humans#1628) — before that the section was
/// admin-only and carried none. Must stay <c>public</c> — the boot diagnostic's
/// <c>SectionResourceTypes()</c> reads <c>GetExportedTypes()</c> and skips an internal marker
/// in silence. The <c>.cs</c> and the <c>.resx</c> files must sit in the same folder: this
/// type's namespace determines the manifest prefix.
///
/// <para>
/// <c>Settings_TabEvent</c> stays in <c>SharedResource</c> instead — it is a tab label, and a
/// label may come from any contributing section, not just this one, so it must resolve
/// against the one resource set every section can reach (design §15 step 3b, carve by
/// renderer). <c>Settings_NoTabs</c> is this section's own empty-state string, rendered by
/// this section's own <c>SettingsTabsViewComponent</c>, so it lives here.
/// </para>
/// </summary>
public sealed class SettingsResource;
