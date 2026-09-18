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
/// <c>Settings_TabEvent</c> and <c>Settings_NoTabs</c> stay in <c>SharedResource</c> instead —
/// the Shell's <c>SettingsTabs</c> view component renders both, and a key rendered outside
/// the section cannot see the section's set (design §15 step 3b, carve by renderer).
/// </para>
/// </summary>
public sealed class SettingsResource;
