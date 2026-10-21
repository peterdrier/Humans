using System.Security.Claims;
using AwesomeAssertions;
using Humans.Cantina.Contracts;
using Humans.Cantina.Controllers;
using Humans.Cantina.Models;
using Humans.Cantina.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Cantina.Tests.Controllers;

/// <summary>
/// <c>/Cantina/Dietary</c> (Users decomposition commit 10): the owner-only form, its
/// "Other needs text" guard, and the "I don't want to answer" opt-out
/// (nobodies-collective/Humans#2104, Docs/features/dietary-nudge.md).
/// </summary>
public class CantinaProfileControllerTests
{
    private readonly IDietaryProfileManagement _dietary = Substitute.For<IDietaryProfileManagement>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly CantinaProfileController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public CantinaProfileControllerTests()
    {
        _dietary.SaveAsync(Arg.Any<Guid>(), Arg.Any<DietaryProfileInfo>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var localizer = Substitute.For<IStringLocalizer<CantinaResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new CantinaProfileController(
            _dietary, _users, localizer, NullLogger<CantinaProfileController>.Instance);

        // SetError on HumansControllerBase resolves ILoggerFactory off RequestServices and reads
        // ActionDescriptor.ActionName for its debug log; wire both so the failure paths don't NRE.
        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "TestAuth")),
            RequestServices = services.BuildServiceProvider(),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Dietary" },
        };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        _controller.Url = Substitute.For<IUrlHelper>();

        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = _userId, BurnerName = "Test Human", PreferredLanguage = "en" },
                [], [], [], null, []));
    }

    [HumansFact]
    public void Controller_requires_sign_in_and_every_post_requires_antiforgery()
    {
        typeof(CantinaProfileController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Should().ContainSingle("the page is the signed-in member's own, never anonymous");
        typeof(CantinaProfileController).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Length > 0)
            .Should().NotBeEmpty()
            .And.OnlyContain(m => m.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).Length > 0);
    }

    [HumansFact]
    public async Task Get_prefills_the_members_own_profile_and_carries_the_return_route()
    {
        _dietary.GetDietaryProfileAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DietaryProfileInfo(_userId, "Vegan", ["Peanut"], null, ["Other"], "Fructose"));

        var result = await _controller.Dietary(returnAction: "shifts");

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<DietaryViewModel>().Subject;
        model.DietaryPreference.Should().Be("Vegan");
        model.Allergies.Should().Equal("Peanut");
        model.IntoleranceOtherText.Should().Be("Fructose");
        model.ReturnAction.Should().Be("shifts");
        model.DeclinedToAnswer.Should().BeFalse();
    }

    [HumansFact]
    public async Task Get_shows_a_prior_opt_out()
    {
        _dietary.GetDietaryProfileAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new DietaryProfileInfo(_userId, null, [], null, [], null, DeclinedToAnswer: true));

        var result = await _controller.Dietary();

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<DietaryViewModel>()
            .Which.DeclinedToAnswer.Should().BeTrue();
    }

    [HumansFact]
    public async Task Post_ValidSave_ReturnActionShifts_RedirectsToShifts()
    {
        var model = MakeValidModel();
        model.ReturnAction = "shifts";

        var result = await _controller.Dietary(model);

        await _dietary.Received(1).SaveAsync(_userId,
            Arg.Is<DietaryProfileInfo>(a => a.UserId == _userId && a.DietaryPreference == "Omnivore"), Arg.Any<CancellationToken>());
        AssertRedirect(result, "Index", "Shifts");
    }

    [HumansFact]
    public async Task Post_ValidSave_NoReturnAction_RedirectsToHome()
    {
        var result = await _controller.Dietary(MakeValidModel());

        await _dietary.Received(1).SaveAsync(_userId, Arg.Any<DietaryProfileInfo>(), Arg.Any<CancellationToken>());
        AssertRedirect(result, "Index", "Home");
    }

    [HumansFact]
    public async Task Post_ValidationFails_DoesNotSave()
    {
        _controller.ModelState.AddModelError("DietaryPreference", "Required");

        var result = await _controller.Dietary(MakeValidModel());

        result.Should().BeOfType<ViewResult>();
        await _dietary.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Post_OtherWithoutText_IsAFieldError_AndNothingIsSaved(bool allergy)
    {
        var model = MakeValidModel();
        if (allergy) model.Allergies = ["Other"]; else model.Intolerances = ["Other"];

        var result = await _controller.Dietary(model);

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState[allergy ? nameof(DietaryViewModel.AllergyOtherText) : nameof(DietaryViewModel.IntoleranceOtherText)]!
            .Errors.Should().ContainSingle().Which.ErrorMessage.Should()
            .Be(allergy ? "Cantina_Dietary_AllergyOther_Required" : "Cantina_Dietary_IntoleranceOther_Required");
        await _dietary.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Post_ServiceRefusal_RerendersWithTheError()
    {
        _dietary.SaveAsync(Arg.Any<Guid>(), Arg.Any<DietaryProfileInfo>(), Arg.Any<CancellationToken>())
            .Returns("Cantina_Dietary_IntoleranceOther_Required");
        var model = MakeValidModel();

        var result = await _controller.Dietary(model);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        _controller.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Cantina_Dietary_IntoleranceOther_Required");
    }

    [HumansTheory]
    [Xunit.InlineData("shifts", "Shifts")]
    [Xunit.InlineData(null, "Home")]
    public async Task Post_DeclineToAnswer_RecordsTheOptOut_WithoutValidatingAnswers(string? returnAction, string controller)
    {
        // The banner posts only the choice: no preference, which the form would otherwise require.
        _controller.ModelState.AddModelError("DietaryPreference", "Required");
        var model = new DietaryViewModel { DeclineToAnswer = true, ReturnAction = returnAction };

        var result = await _controller.Dietary(model);

        await _dietary.Received(1).DeclineAsync(_userId, Arg.Any<CancellationToken>());
        await _dietary.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, Arg.Any<CancellationToken>());
        AssertRedirect(result, "Index", controller);
    }

    private static void AssertRedirect(IActionResult result, string action, string controller)
    {
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(action);
        redirect.ControllerName.Should().Be(controller);
    }

    private static DietaryViewModel MakeValidModel() => new() { DietaryPreference = "Omnivore" };
}
