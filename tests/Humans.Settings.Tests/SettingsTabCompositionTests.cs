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
        var a = new FakeSettings(new SettingsTab("first", "First", typeof(FirstPanel), Weight: 0));
        var b = new FakeSettings(
            new SettingsTab("second", "Second", typeof(SecondPanel), Weight: 0),
            new SettingsTab("heavy", "Heavy", typeof(HeavyPanel), Weight: 10));

        var tabs = await SettingsTabComposition.ComposeAsync([a, b], AlwaysAllow(), User);

        tabs.Select(t => t.Key).Should().Equal("first", "second", "heavy");
    }

    [HumansFact]
    public async Task Drops_Tab_When_Policy_Fails()
    {
        var contributor = new FakeSettings(new SettingsTab("gated", "Gated", typeof(GatedPanel), Policy: "SomePolicy"));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var tabs = await SettingsTabComposition.ComposeAsync([contributor], auth, User);

        tabs.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Keeps_Tab_With_No_Policy()
    {
        var contributor = new FakeSettings(new SettingsTab("open", "Open", typeof(OpenPanel)));
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var tabs = await SettingsTabComposition.ComposeAsync([contributor], auth, User);

        tabs.Should().ContainSingle(t => t.Key == "open");
    }

    [HumansFact]
    public async Task Deduplicates_By_Key_First_Wins()
    {
        var first = new FakeSettings(new SettingsTab("shared", "First Claim", typeof(FirstPanel)));
        var second = new FakeSettings(new SettingsTab("shared", "Second Claim", typeof(SecondPanel)));

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

    private sealed class FirstPanel;
    private sealed class SecondPanel;
    private sealed class HeavyPanel;
    private sealed class GatedPanel;
    private sealed class OpenPanel;

    private static IAuthorizationService AlwaysAllow()
    {
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return auth;
    }
}
