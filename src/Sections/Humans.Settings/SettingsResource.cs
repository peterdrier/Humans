namespace Humans.Settings;

/// <summary>
/// The .resx files sit beside this file: its namespace sets the manifest prefix. Public because the boot localization
/// check finds markers through <c>GetExportedTypes()</c>. Tab labels such as <c>Settings_TabEvent</c> stay in
/// <c>SharedResource</c>: any section may contribute a tab.
/// </summary>
public sealed class SettingsResource;
