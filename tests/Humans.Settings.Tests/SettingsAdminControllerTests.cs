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
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

/// <summary>
/// The screen never offers a blank form that would mint an event id Shifts does
/// not have, and a save that deactivates a row leaves it reachable by id.
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

    private static EventSettingsViewModel MakeForm(Guid? id, bool isActive) => new()
    {
        Id = id,
        EventName = "Nowhere 2026",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = "2026-07-09",
        IsActive = isActive,
    };

    [HumansFact]
    public void Index_Get_WithoutAnId_RedirectsToTheSettingsPageEventTab()
    {
        // peterdrier/Humans#1628: the screen is superseded by the /Settings#event tab —
        // one canonical URL per page (memory/product/no-url-aliases.md).
        var result = BuildSut().Index(id: null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event");
    }

    [HumansFact]
    public void Index_Get_WithAnId_ForwardsItToTheSettingsPageEventTab()
    {
        // A carried, possibly-inactive row named by id must still resolve to that
        // row, not to whichever one the tab shows by default.
        var id = Guid.NewGuid();

        var result = BuildSut().Index(id);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/Settings?event={id}#event");
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
        // The GET action now accepts that same id and forwards it, so this redirect
        // resolves to the row that was just saved, not the active one.
        BuildSut().Index((Guid?)redirect.RouteValues["id"]).Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be($"/Settings?event={id}#event");
        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Id == id && s.Status == EventSettingsStatus.Inactive),
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
            Arg.Any<EventSettingsInfo>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_Post_ActivationConflict_ComesBackAsAFormErrorNotA500()
    {
        // Checking Active on an inactive row while another cycle is Active is an ordinary
        // operator conflict. The service says so by throwing; the screen has to render it.
        const string Conflict =
            "Only one event settings row can be Active at a time — deactivate the current one first.";
        _settings.SaveEventSettingsAsync(Arg.Any<EventSettingsInfo>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(Conflict)));
        var sut = BuildSut();
        var form = MakeForm(Guid.NewGuid(), isActive: true);

        var result = await sut.Index(form, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        sut.ModelState[string.Empty]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Be(Conflict);
    }

    [HumansFact]
    public async Task Index_Post_SavesWithTheAuthenticatedActor()
    {
        // The controller resolves the actor from the claims principal — no separate
        // Users lookup needed just to attribute the audit entry (peterdrier/Humans#1628).
        var sut = BuildSut();
        var actorId = Guid.Parse(((ClaimsIdentity)sut.HttpContext.User.Identity!)
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);

        await sut.Index(MakeForm(Guid.NewGuid(), isActive: false), TestContext.Current.CancellationToken);

        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Any<EventSettingsInfo>(), actorId, Arg.Any<CancellationToken>());
    }
}
