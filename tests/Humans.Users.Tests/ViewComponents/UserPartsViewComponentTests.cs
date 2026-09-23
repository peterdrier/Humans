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
    public void NoContributions_RendersNothing()
    {
        var result = Sut(Contributor()).Invoke(Guid.NewGuid());

        result.Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public void OrdersPartsByWeight_AcrossContributors()
    {
        var model = ModelFrom(
            Contributor(new UserPart(typeof(LatePart), 10), new UserPart(typeof(MiddlePart), 5)),
            Contributor(new UserPart(typeof(EarlyPart), 0)));

        model.Parts.Select(p => p.Component).Should().Equal(typeof(EarlyPart), typeof(MiddlePart), typeof(LatePart));
    }

    [HumansFact]
    public void PassesTheTargetUser_ToTheViewModel()
    {
        var target = Guid.NewGuid();
        var contributor = Contributor(new UserPart(typeof(EarlyPart)));

        var model = ModelFrom(target, contributor);

        model.UserId.Should().Be(target);
        contributor.Received(1).Parts();
    }

    [HumansFact]
    public void ContributorThatThrows_IsSkipped_OthersStillRender()
    {
        var broken = Substitute.For<IUserPart>();
        broken.Parts().Returns(_ => throw new InvalidOperationException("section down"));

        var model = ModelFrom(Guid.NewGuid(), broken, Contributor(new UserPart(typeof(EarlyPart))));

        model.Parts.Should().ContainSingle().Which.Component.Should().Be(typeof(EarlyPart));
    }

    private sealed class EarlyPart;
    private sealed class MiddlePart;
    private sealed class LatePart;

    private static IUserPart Contributor(params UserPart[] parts)
    {
        var contributor = Substitute.For<IUserPart>();
        contributor.Parts().Returns(parts);
        return contributor;
    }

    private static UserPartsViewModel ModelFrom(params IUserPart[] contributors) =>
        ModelFrom(Guid.NewGuid(), contributors);

    private static UserPartsViewModel ModelFrom(Guid userId, params IUserPart[] contributors)
    {
        var result = Sut(contributors).Invoke(userId);

        return result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData!.Model
            .Should().BeOfType<UserPartsViewModel>().Subject;
    }

    private static UserPartsViewComponent Sut(params IUserPart[] contributors) =>
        new(contributors, NullLogger<UserPartsViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
}
