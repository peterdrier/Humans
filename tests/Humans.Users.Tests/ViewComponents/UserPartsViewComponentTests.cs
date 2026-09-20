using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Users.Models;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public class UserPartsViewComponentTests
{
    [HumansFact]
    public async Task NoContributions_RendersNothing()
    {
        var result = await Sut(Contributor()).InvokeAsync(Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public async Task OrdersPartsByWeight_AcrossContributors()
    {
        var model = await ModelFrom(
            Contributor(new UserPart("Late", 10), new UserPart("Middle", 5)),
            Contributor(new UserPart("Early", 0)));

        model.Parts.Select(p => p.ComponentName).Should().Equal(["Early", "Middle", "Late"]);
    }

    [HumansFact]
    public async Task PassesTheTargetUser_NotTheViewer()
    {
        var target = Guid.NewGuid();
        var contributor = Contributor(new UserPart("EventsCard"));

        var model = await ModelFrom(target, contributor);

        model.UserId.Should().Be(target);
        await contributor.Received(1).PartsAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), target);
    }

    [HumansFact]
    public async Task ContributorThatThrows_IsSkipped_OthersStillRender()
    {
        var broken = Substitute.For<IUserPart>();
        broken.PartsAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<Guid>())
            .Returns<IEnumerable<UserPart>>(_ => throw new InvalidOperationException("section down"));

        var model = await ModelFrom(Guid.NewGuid(), broken, Contributor(new UserPart("EventsCard")));

        model.Parts.Should().ContainSingle().Which.ComponentName.Should().Be("EventsCard");
    }

    private static IUserPart Contributor(params UserPart[] parts)
    {
        var contributor = Substitute.For<IUserPart>();
        contributor.PartsAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<Guid>())
            .Returns(ValueTask.FromResult<IEnumerable<UserPart>>(parts));
        return contributor;
    }

    private static Task<UserPartsViewModel> ModelFrom(params IUserPart[] contributors) =>
        ModelFrom(Guid.NewGuid(), contributors);

    private static async Task<UserPartsViewModel> ModelFrom(Guid userId, params IUserPart[] contributors)
    {
        var result = await Sut(contributors).InvokeAsync(userId);

        return result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData!.Model
            .Should().BeOfType<UserPartsViewModel>().Subject;
    }

    private static UserPartsViewComponent Sut(params IUserPart[] contributors) =>
        new(contributors, Substitute.For<IServiceProvider>(), NullLogger<UserPartsViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
}
