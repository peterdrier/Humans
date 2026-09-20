using Humans.Base.Interfaces;

namespace Humans.Settings;

/// <summary>Layout chrome contribution — the Settings link in the signed-in user menu.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.UserMenu, "SettingsUserMenu", Weight: 10)];
}
