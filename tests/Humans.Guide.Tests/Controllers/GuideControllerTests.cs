using AwesomeAssertions;
using Humans.Guide.Controllers;
using Humans.Guide.Services;
using Humans.Guide.Models;
using Humans.Base;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Guide.Tests.Controllers;

public class GuideControllerTests
{
    private readonly IGuideContentService _content = Substitute.For<IGuideContentService>();
    private readonly IGuideRoleResolver _roles = Substitute.For<IGuideRoleResolver>();

    private readonly ResourceManagerStringLocalizerFactory _localizers = new(
        Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);

    private GuideController CreateController() => new(_content, _roles,
        new StringLocalizer<GuideResource>(_localizers), new StringLocalizer<SharedResource>(_localizers))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [HumansTheory]
    [InlineData("en", "Getting Started", "Start here", "Teams")]
    [InlineData("es", "Primeros pasos", "Empieza aquí", "Equipos")]
    [InlineData("de", "Erste Schritte", "Hier beginnen", "Teams")]
    [InlineData("it", "Primi passi", "Inizia qui", "Team")]
    [InlineData("fr", "Premiers pas", "Commencez ici", "Équipes")]
    [InlineData("ca", "Primers passos", "Comença aquí", "Equips")]
    public async Task Document_LocalizesNavigationWhilePreservingContentAndRoutes(
        string culture, string gettingStarted, string startGroup, string teamTitle)
    {
        const string html = "<p>Original authored content</p>";
        _content.GetPageAsync(Arg.Any<string>(), Arg.Any<GuideRoleContext>(), Arg.Any<CancellationToken>()).Returns(html);
        _roles.ResolveAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(GuideRoleContext.Anonymous);
        var controller = CreateController();

        using var scope = new CultureScope(culture);
        var result = await controller.Document("teAMs", Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<GuideViewModel>().Subject;
        model.Title.Should().Be(teamTitle);
        model.Sidebar.Entries[0].DisplayName.Should().Be(gettingStarted);
        model.Sidebar.Entries[0].Group.Should().Be(startGroup);
        model.Sidebar.Entries.Single(e => string.Equals(e.Stem, "Teams", StringComparison.Ordinal)).DisplayName.Should().Be(teamTitle);
        model.Sidebar.ActiveStem.Should().Be("Teams");
        model.FileStem.Should().Be("Teams");
        model.Html.ToString().Should().Be(html);
        await _content.Received(1).GetPageAsync("Teams", GuideRoleContext.Anonymous, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Document_UnknownStem_Returns404AndTheNotFoundView()
    {
        var controller = CreateController();

        var result = await controller.Document("NoSuchPage", Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("NotFound");
        await _content.DidNotReceive().GetPageAsync(Arg.Any<string>(), Arg.Any<GuideRoleContext>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Document_ContentUnavailable_Returns503AndTheUnavailableView()
    {
        _roles.ResolveAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(GuideRoleContext.Anonymous);
        _content.GetPageAsync("Teams", Arg.Any<GuideRoleContext>(), Arg.Any<CancellationToken>())
            .Throws(new GuideContentUnavailableException("cache is cold and GitHub is unreachable"));
        var controller = CreateController();

        var result = await controller.Document("Teams", Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("Unavailable");
    }

    [HumansFact]
    public async Task Document_StemInAnyCasing_ResolvesToTheCanonicalSpelling()
    {
        _content.GetPageAsync(Arg.Any<string>(), Arg.Any<GuideRoleContext>(), Arg.Any<CancellationToken>()).Returns("<p>x</p>");
        _roles.ResolveAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(GuideRoleContext.Anonymous);
        var controller = CreateController();

        await controller.Document("teAMs", Xunit.TestContext.Current.CancellationToken);

        // The canonical spelling is what reaches the cache key and the GitHub path.
        await _content.Received().GetPageAsync("Teams", Arg.Any<GuideRoleContext>(), Arg.Any<CancellationToken>());
    }
}
