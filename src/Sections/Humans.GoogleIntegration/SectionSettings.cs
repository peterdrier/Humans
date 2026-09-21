using Humans.Base.Authorization;
using Humans.Settings.Contracts;

namespace Humans.GoogleIntegration;

/// <summary>
/// GoogleIntegration's /Settings tab (peterdrier/Humans#1634) — the settings that used
/// to live at /Google/SyncSettings.
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("google-sync", "Settings_TabGoogleSync", "GoogleSyncSettingsTab", PolicyNames.AdminOnly)
    ];
}
