using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Settings.ViewComponents;

namespace Humans.Settings.Tests;

public sealed class SectionChromeTests
{
    [HumansFact]
    public void Components_ReturnsTheSettingsUserMenuLinkInTheUserMenuSlot()
    {
        var components = new SectionChrome().Components().ToList();

        var component = components.Should().ContainSingle().Which;
        component.Slot.Should().Be(ChromeSlots.UserMenu);
        component.Component.Should().Be(typeof(SettingsUserMenuViewComponent));
    }
}
