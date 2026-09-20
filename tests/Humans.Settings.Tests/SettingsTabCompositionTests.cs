using System.Security.Claims;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;

namespace Humans.Settings.Tests;

public class SettingsTabCompositionTests
{
    private sealed class FakeSettings(params SettingsTab[] tabs) : ISectionSettings
    {
        public IEnumerable<SettingsTab> Tabs() => tabs;
    }

    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity());

    [HumansFact]
    public async Task Orders_By_Weight_Stable_For_Equal_Weights()
    {
        var a = new FakeSettings(new SettingsTab("first", "First", "FirstPanel", Weight: 0));
        var b = new FakeSettings(
            new SettingsTab("second", "Second", "SecondPanel", Weight: 0),
            new SettingsTab("heavy", "Heavy", "HeavyPanel", Weight: 10));

        var tabs = await SettingsTabComposition.ComposeAsync([a, b], AlwaysAllow(), User);

        tabs.Select(t => t.Key).Should().Equal("first", "second", "heavy");
    }

    [HumansFact]
    public async Task Drops_Tab_When_Policy_Fails()
    {
        var contributor = new FakeSettings(new SettingsTab("gated", "Gated", "GatedPanel", Policy: "SomePolicy"));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var tabs = await SettingsTabComposition.ComposeAsync([contributor], auth, User);

        tabs.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Keeps_Tab_With_No_Policy()
    {
        var contributor = new FakeSettings(new SettingsTab("open", "Open", "OpenPanel"));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var tabs = await SettingsTabComposition.ComposeAsync([contributor], auth, User);

        tabs.Should().ContainSingle(t => t.Key == "open");
    }

    [HumansFact]
    public async Task Deduplicates_By_Key_First_Wins()
    {
        var first = new FakeSettings(new SettingsTab("shared", "First Claim", "FirstPanel"));
        var second = new FakeSettings(new SettingsTab("shared", "Second Claim", "SecondPanel"));

        var tabs = await SettingsTabComposition.ComposeAsync([first, second], AlwaysAllow(), User);

        tabs.Should().ContainSingle();
        tabs.Single().Label.Should().Be("First Claim");
    }

    [HumansFact]
    public async Task Empty_Contributors_Yields_Empty_List()
    {
        var tabs = await SettingsTabComposition.ComposeAsync([], AlwaysAllow(), User);

        tabs.Should().BeEmpty();
    }

    private static IAuthorizationService AlwaysAllow()
    {
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return auth;
    }
}
