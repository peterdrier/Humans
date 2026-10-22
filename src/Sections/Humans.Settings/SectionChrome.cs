using Humans.Base.Interfaces;
using Humans.Settings.ViewComponents;

namespace Humans.Settings;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.UserMenu, typeof(SettingsUserMenuViewComponent), Weight: 10)];
}
