using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Web.Controllers;
using Humans.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Filters;

/// <summary>
/// <see cref="SectionRoutes"/> and the link tag helpers against the app's real routes and layout. The signed-out
/// layout links to Legal, so Consent stands in for a switched-off section here even though no admin can pick it.
/// </summary>
public sealed class SectionLinkRenderingTests(NoDatabaseFactory factory) : IClassFixture<NoDatabaseFactory>
{
    private static readonly string Consent = SectionSwitchboard.SectionNameOf(typeof(Humans.Consent.Section));
    private static readonly string Shell = SectionSwitchboard.SectionNameOf(typeof(HomeController));

    [HumansFact]
    public void TheAppsRoutes_ResolveToTheSectionThatServesThem()
    {
        using var client = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<SectionRoutes>();

        routes.SectionOfController("Legal").Should().Be(Consent);
        routes.SectionOfController("Team").Should().Be(SectionSwitchboard.SectionNameOf(typeof(Humans.Teams.Section)));
        routes.SectionOfUrl("/Legal").Should().Be(Consent);
        routes.SectionOfUrl("/").Should().Be(Shell);
        routes.SectionOfUrl("/Home/Privacy").Should().Be(Shell);
        routes.SectionOfUrl("/Account/Login?returnUrl=%2F").Should().Be(Shell);
        routes.SectionOfUrl("/css/site.css").Should().BeNull();
    }

    [HumansFact]
    public async Task ALayoutLinkIntoASwitchedOffSection_GoesWithItsListItem()
    {
        var before = await LoginPageAsync();
        var after = await LoginPageAsync(Consent);

        before.Should().Contain("href=\"/Legal\"", "the test needs a link to lose");
        after.Should().NotContain("href=\"/Legal\"");
        Regex.IsMatch(after, @"<li[^>]*>\s*</li>", RegexOptions.None, TimeSpan.FromSeconds(1))
            .Should().BeFalse("the list item goes with its only link");
        after.Should().NotContain("hide-with-links");
    }

    private async Task<string> LoginPageAsync(params string[] switchedOff)
    {
        var switches = Substitute.For<ISectionSwitches>();
        switches.GetSwitchedOffAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(switchedOff, StringComparer.OrdinalIgnoreCase));
        using var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.RemoveAll<ISectionSwitches>().AddSingleton(switches)))
            .CreateClient();

        return await client.GetStringAsync("/Account/Login");
    }
}
