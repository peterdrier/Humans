using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.GoogleIntegration.ViewComponents;

namespace Humans.GoogleIntegration;

internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("google-sync", "Settings_TabGoogleSync", typeof(GoogleSyncSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];
}
