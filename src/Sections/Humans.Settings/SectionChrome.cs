using Humans.Base.Interfaces;
using Humans.Settings.ViewComponents;

namespace Humans.Settings;

/// <summary>Layout chrome contribution — the Settings link in the signed-in user menu.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.UserMenu, typeof(SettingsUserMenuViewComponent), Weight: 10)];
}
