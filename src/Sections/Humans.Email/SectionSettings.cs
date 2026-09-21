using Humans.Base.Authorization;
using Humans.Settings.Contracts;

namespace Humans.Email;

/// <summary>
/// Email's /Settings tab (peterdrier/Humans#1634) — the send-pause toggle that used to
/// live on the /Email/EmailOutbox dashboard header.
/// </summary>
internal sealed class SectionSettings : ISectionSettings
{
    public IEnumerable<SettingsTab> Tabs() =>
    [
        new SettingsTab("email", "Settings_TabEmail", "EmailPauseSettingsTab", PolicyNames.AdminOnly)
    ];
}
