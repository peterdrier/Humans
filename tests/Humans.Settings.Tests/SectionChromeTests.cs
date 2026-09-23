using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Settings.Tests;

public sealed class SectionChromeTests
{
    [HumansFact]
    public void Components_ReturnsTheSettingsUserMenuLinkInTheUserMenuSlot()
    {
        var components = new SectionChrome().Components().ToList();

        var component = components.Should().ContainSingle().Which;
        component.Slot.Should().Be(ChromeSlots.UserMenu);
    }
}
