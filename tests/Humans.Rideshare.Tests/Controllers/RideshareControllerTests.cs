using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Rideshare.Controllers;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Rideshare.Tests.Controllers;

/// <summary>
/// The controller's error-contract mapping: 404 / 403 pass through, a rule becomes a toast on
/// Mine or a model error on the form. The rules themselves live in the service tests.
/// </summary>
public sealed class RideshareControllerTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IRideshareService _rideshare = Substitute.For<IRideshareService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IStringLocalizer<RideshareResource> _localizer = Substitute.For<IStringLocalizer<RideshareResource>>();
    private readonly Guid _me = Guid.NewGuid();

    public RideshareControllerTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), "L:" + ci.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci => new LocalizedString(ci.Arg<string>(), "L:" + ci.Arg<string>()));
        _users.GetUserInfoAsync(_me, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(User(_me)));
        _rideshare.GetActiveYearAsync(Arg.Any<CancellationToken>()).Returns(2026);
    }

    [HumansFact]
    public async Task Anonymous_IsChallenged()
    {
        var result = await BuildController(signedIn: false).Accept(Guid.NewGuid(), Ct);

        result.Should().BeOfType<ChallengeResult>();
        await _rideshare.DidNotReceiveWithAnyArgs().AcceptInterestAsync(default, default, default);
    }

    [HumansFact]
    public async Task Accept_OnSuccess_ToastsAndRedirectsToMine()
    {
        var controller = BuildController();

        var result = await controller.Accept(Guid.NewGuid(), Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Mine");
        controller.TempData[TempDataKeys.SuccessMessage].Should().Be("L:Rideshare_InterestAccepted");
        controller.TempData[TempDataKeys.ErrorMessage].Should().BeNull();
    }

    [HumansFact]
    public async Task Accept_MapsNotFoundAndForbidden_ToTheirStatusCodes()
    {
        var missing = Guid.NewGuid();
        var notMine = Guid.NewGuid();
        _rideshare.AcceptInterestAsync(missing, _me, Arg.Any<CancellationToken>()).ThrowsAsync(new KeyNotFoundException());
        _rideshare.AcceptInterestAsync(notMine, _me, Arg.Any<CancellationToken>()).ThrowsAsync(new UnauthorizedAccessException());
        var controller = BuildController();

        (await controller.Accept(missing, Ct)).Should().BeOfType<NotFoundResult>();
        (await controller.Accept(notMine, Ct)).Should().BeOfType<ForbidResult>();
        controller.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Accept_MapsARule_ToAnErrorToastOnMine()
    {
        _rideshare.AcceptInterestAsync(Arg.Any<Guid>(), _me, Arg.Any<CancellationToken>())
            .ThrowsAsync(new RideshareRuleException("Rideshare_Error_NotEnoughSeats"));
        var controller = BuildController();

        var result = await controller.Accept(Guid.NewGuid(), Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Mine");
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("L:Rideshare_Error_NotEnoughSeats");
        controller.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
    }

    [HumansFact]
    public async Task Interest_WithoutATrip_SendsTheHumanBackToTheBoard()
    {
        var controller = BuildController();

        var result = await controller.Interest(Guid.Empty, null, 1, null, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("L:Rideshare_PickAnOffer");
        await _rideshare.DidNotReceiveWithAnyArgs().ExpressInterestAsync(default, default, default, default, default, default);
    }

    [HumansFact]
    public async Task OfferPost_AnUnparsableDate_RerendersTheFormWithADateError()
    {
        var model = new OfferFormViewModel { MemberPlaceLabel = "Paris", DepartureDate = "3 July" };
        var controller = BuildController();

        var result = await controller.Offer(model, Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        controller.ModelState[nameof(model.DepartureDate)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("L:Rideshare_InvalidDate");
        await _rideshare.DidNotReceiveWithAnyArgs().CreateOfferAsync(default, default, default!, default);
    }

    [HumansFact]
    public async Task OfferPost_ARule_RerendersTheFormWithAModelError()
    {
        _rideshare.CreateOfferAsync(_me, 2026, Arg.Any<TripSave>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RideshareRuleException("Rideshare_Error_PlaceNotFound", "Atlantis"));
        var model = new OfferFormViewModel { MemberPlaceLabel = "Atlantis", DepartureDate = "2026-07-03" };
        var controller = BuildController();

        var result = await controller.Offer(model, Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("L:Rideshare_Error_PlaceNotFound");
        controller.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task OfferPost_WithAnId_Updates_AndRedirectsToMine()
    {
        var id = Guid.NewGuid();
        var model = new OfferFormViewModel { Id = id, MemberPlaceLabel = "Paris", DepartureDate = "2026-07-03" };
        var controller = BuildController();

        var result = await controller.Offer(model, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Mine");
        await _rideshare.Received(1).UpdateOfferAsync(id, _me, Arg.Is<TripSave>(s => s.MemberPlaceLabel == "Paris"), Arg.Any<CancellationToken>());
        await _rideshare.DidNotReceiveWithAnyArgs().CreateOfferAsync(default, default, default!, default);
        controller.TempData[TempDataKeys.SuccessMessage].Should().Be("L:Rideshare_OfferSaved");
    }

    private RideshareController BuildController(bool signedIn = true)
    {
        var controller = new RideshareController(
            _rideshare, _users, _localizer, Substitute.For<IClock>(), NullLogger<RideshareController>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (signedIn)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _me.ToString())], authenticationType: "test"));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" },
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return controller;
    }

    private static UserInfo User(Guid id) => new(
        id, "Ada", false, "en", null, Now,
        null, null, null, null, null, false, null, false, null, null, null,
        null, null, null, [], [], [], null, []);
}
