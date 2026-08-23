using System.Security.Claims;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Settings.Controllers;
using Humans.Settings.Models;
using Humans.Settings.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NodaTime;
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

/// <summary>
/// The screen's two review findings (nobodies-collective/Humans#1104): it never
/// offers a blank form that would mint an event id Shifts does not have, and a
/// save it deactivates stays reachable by id instead of vanishing.
/// </summary>
public sealed class SettingsAdminControllerTests
{
    private readonly ISettingsWriteService _settings = Substitute.For<ISettingsWriteService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();

    private SettingsAdminController BuildSut()
    {
        var controller = new SettingsAdminController(_settings, _users);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static EventSettingsInfo MakeInfo(Guid id, EventSettingsStatus status) => new(
        Id: id,
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 9),
        BuildStartOffset: -25,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -25,
        SetupWeekStartOffset: -16,
        PreEventWeekStartOffset: -9,
        FinishingWeekendStartOffset: -4,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        Status: status);

    private static EventSettingsViewModel MakeForm(Guid? id, bool isActive) => new()
    {
        Id = id,
        EventName = "Nowhere 2026",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = "2026-07-09",
        IsActive = isActive,
    };

    [HumansFact]
    public async Task Index_WithNoActiveRow_ShowsTheCarryPromptInsteadOfABlankForm()
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns((EventSettingsInfo?)null);

        var result = await BuildSut().Index(id: null, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>()
            .Which.ViewName.Should().Be("NoEvent");
    }

    [HumansFact]
    public async Task Index_WithAnId_LoadsThatRowEvenWhenItIsInactive()
    {
        var id = Guid.NewGuid();
        _settings.GetEventSettingsByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(MakeInfo(id, EventSettingsStatus.Inactive));

        var result = await BuildSut().Index(id, TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model
            .Should().BeOfType<EventSettingsViewModel>().Which;
        model.Id.Should().Be(id);
        model.IsActive.Should().BeFalse();
        await _settings.DidNotReceive().GetActiveEventSettingsAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_WithAnUnknownId_ShowsTheCarryPrompt()
    {
        _settings.GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((EventSettingsInfo?)null);

        var result = await BuildSut().Index(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("NoEvent");
    }

    [HumansFact]
    public async Task Index_Post_RedirectsBackToTheRowItJustSaved()
    {
        // Deactivating takes the row off the bare GET; without the id it would be
        // stranded and the next save would mint another one.
        var id = Guid.NewGuid();

        var result = await BuildSut().Index(MakeForm(id, isActive: false), TestContext.Current.CancellationToken);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Which;
        redirect.ActionName.Should().Be(nameof(SettingsAdminController.Index));
        redirect.RouteValues!["id"].Should().Be(id);
        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Id == id && s.Status == EventSettingsStatus.Inactive),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_Post_WithoutAnId_IsRefusedInsteadOfMintingOne()
    {
        var sut = BuildSut();

        var result = await sut.Index(MakeForm(id: null, isActive: true), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
        sut.ModelState[nameof(EventSettingsViewModel.Id)]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Contain("/Settings/Admin/Carry");
        await _settings.DidNotReceive().SaveEventSettingsAsync(
            Arg.Any<EventSettingsInfo>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_Post_ActivationConflict_ComesBackAsAFormErrorNotA500()
    {
        // Checking Active on an inactive row while another cycle is Active is an ordinary
        // operator conflict. The service says so by throwing; the screen has to render it.
        const string Conflict =
            "Only one event settings row can be Active at a time — deactivate the current one first.";
        _settings.SaveEventSettingsAsync(Arg.Any<EventSettingsInfo>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(Conflict)));
        var sut = BuildSut();
        var form = MakeForm(Guid.NewGuid(), isActive: true);

        var result = await sut.Index(form, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        sut.ModelState[string.Empty]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Be(Conflict);
    }
}
