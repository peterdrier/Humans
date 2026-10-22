using Humans.Base.Authorization;
using Humans.Settings.Contracts;
using Humans.Email.ViewComponents;

namespace Humans.Email;

internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("email", "Settings_TabEmail", typeof(EmailPauseSettingsTabViewComponent), PolicyNames.AdminOnly)
    ];
}
