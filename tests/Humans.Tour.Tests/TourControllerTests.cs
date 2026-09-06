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
    public void Index_is_routed_as_GET_Tour()
    {
        typeof(TourController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Should().ContainSingle().Which.As<RouteAttribute>().Template.Should().Be("Tour");

        typeof(TourController).GetMethod(nameof(TourController.Index))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Should().ContainSingle().Which.As<HttpGetAttribute>().Template.Should().Be("",
                because: "the page lives at /Tour itself; the render test that proves it end to end is local-only");
    }

    [HumansFact]
    public void Index_returns_the_default_view()
    {
        var result = new TourController().Index();

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().BeNull(because: "the page is Views/Tour/Index.cshtml, found by convention");
    }
}
