using AwesomeAssertions;
using Humans.Guide.Controllers;
using Humans.Guide.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Guide.Tests.Controllers;

public class GuideControllerTests
{
    private readonly IGuideContentService _content = Substitute.For<IGuideContentService>();
    private readonly IGuideRoleResolver _roles = Substitute.For<IGuideRoleResolver>();

    private GuideController CreateController() => new(_content, _roles)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [HumansFact]
    public async Task Document_UnknownStem_Returns404AndTheNotFoundView()
    {
        var controller = CreateController();

        var result = await controller.Document("NoSuchPage", Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("NotFound");
        await _content.DidNotReceive().GetRenderedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Document_ContentUnavailable_Returns503AndTheUnavailableView()
    {
        _content.GetRenderedAsync("Teams", Arg.Any<CancellationToken>())
            .Throws(new GuideContentUnavailableException("cache is cold and GitHub is unreachable"));
        var controller = CreateController();

        var result = await controller.Document("Teams", Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("Unavailable");
    }

    [HumansFact]
    public async Task Document_StemInAnyCasing_ResolvesToTheCanonicalSpelling()
    {
        _content.GetRenderedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("<p>x</p>");
        _roles.ResolveAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(GuideRoleContext.Anonymous);
        var controller = CreateController();

        await controller.Document("teAMs", Xunit.TestContext.Current.CancellationToken);

        // The canonical spelling is what reaches the cache key and the GitHub path.
        await _content.Received().GetRenderedAsync("Teams", Arg.Any<CancellationToken>());
    }
}
