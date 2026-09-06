using AwesomeAssertions;
using Humans.Tour.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tour.Tests;

/// <summary>The public page: reachable signed out, no model, no services.</summary>
public class TourControllerTests
{
    [HumansFact]
    public void Controller_allows_anonymous_at_class_scope()
    {
        typeof(TourController)
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .Should().ContainSingle(because: "the Tour is the one page a visitor may read before signing in");
    }

    [HumansFact]
    public void Index_returns_the_default_view_with_no_model()
    {
        var result = new TourController().Index();

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().BeNull(because: "the page is Views/Tour/Index.cshtml, found by convention");
        view.Model.Should().BeNull(because: "the section owns no data and calls no services");
    }
}
