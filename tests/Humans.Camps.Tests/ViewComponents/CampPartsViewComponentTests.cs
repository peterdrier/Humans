using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Camps.Models;
using Humans.Camps.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Camps.Tests.ViewComponents;

public class CampPartsViewComponentTests
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
            Contributor(new CampPart(typeof(LatePart), 10), new CampPart(typeof(MiddlePart), 5)),
            Contributor(new CampPart(typeof(EarlyPart), 0)));

        model.Parts.Select(p => p.Component).Should().Equal(typeof(EarlyPart), typeof(MiddlePart), typeof(LatePart));
    }

    [HumansFact]
    public void PassesTheCampId_AsTheArgs()
    {
        var campId = Guid.NewGuid();
        var contributor = Contributor(new CampPart(typeof(EarlyPart)));

        var model = ModelFrom(campId, contributor);

        model.Args.Should().Be(new CampPartArgs(campId));
        contributor.Received(1).Parts();
    }

    [HumansFact]
    public void ContributorThatThrows_IsSkipped_OthersStillRender()
    {
        var broken = Substitute.For<ICampPart>();
        broken.Parts().Returns(_ => throw new InvalidOperationException("section down"));

        var model = ModelFrom(Guid.NewGuid(), broken, Contributor(new CampPart(typeof(EarlyPart))));

        model.Parts.Should().ContainSingle().Which.Component.Should().Be(typeof(EarlyPart));
    }

    private sealed class EarlyPart;
    private sealed class MiddlePart;
    private sealed class LatePart;

    private static ICampPart Contributor(params CampPart[] parts)
    {
        var contributor = Substitute.For<ICampPart>();
        contributor.Parts().Returns(parts);
        return contributor;
    }

    private static CampPartsViewModel ModelFrom(params ICampPart[] contributors) =>
        ModelFrom(Guid.NewGuid(), contributors);

    private static CampPartsViewModel ModelFrom(Guid campId, params ICampPart[] contributors)
    {
        var result = Sut(contributors).Invoke(campId);

        return result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData!.Model
            .Should().BeOfType<CampPartsViewModel>().Subject;
    }

    private static CampPartsViewComponent Sut(params ICampPart[] contributors) =>
        new(contributors, NullLogger<CampPartsViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
}
