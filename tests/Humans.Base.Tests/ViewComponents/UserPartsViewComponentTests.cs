using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Base.Tests.ViewComponents;

public class UserPartsViewComponentTests
{
    private const string Slot = UserPartSlots.Profile;

    [HumansFact]
    public void NoContributions_RendersNothing()
    {
        var result = Sut(Contributor()).Invoke(Slot, Guid.NewGuid(), ProfileCardViewMode.Public);

        result.Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public void OnlyPartsForTheRequestedSlot_Render()
    {
        var result = Sut(Contributor(new UserPart(UserPartSlots.AdminDetail, typeof(EarlyPart))))
            .Invoke(Slot, Guid.NewGuid(), ProfileCardViewMode.Public);

        result.Should().BeOfType<ContentViewComponentResult>(
            because: "a part contributed to another slot must not leak into this one");
    }

    [HumansFact]
    public void OrdersPartsByWeight_AcrossContributors()
    {
        var model = ModelFrom(
            Contributor(new UserPart(Slot, typeof(LatePart), 10), new UserPart(Slot, typeof(MiddlePart), 5)),
            Contributor(new UserPart(Slot, typeof(EarlyPart), 0)));

        model.Parts.Select(p => p.Component).Should().Equal(typeof(EarlyPart), typeof(MiddlePart), typeof(LatePart));
    }

    [HumansFact]
    public void PassesTheTargetUserAndAudience_AsTheArgs()
    {
        var target = Guid.NewGuid();
        var contributor = Contributor(new UserPart(Slot, typeof(EarlyPart)));

        var model = ModelFrom(target, contributor);

        model.Args.Should().Be(new UserPartArgs(target, ProfileCardViewMode.Admin));
        contributor.Received(1).Parts();
    }

    [HumansFact]
    public void ContributorThatThrows_IsSkipped_OthersStillRender()
    {
        var broken = Substitute.For<IUserPart>();
        broken.Parts().Returns(_ => throw new InvalidOperationException("section down"));

        var model = ModelFrom(Guid.NewGuid(), broken, Contributor(new UserPart(Slot, typeof(EarlyPart))));

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
        var result = Sut(contributors).Invoke(Slot, userId, ProfileCardViewMode.Admin);

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
