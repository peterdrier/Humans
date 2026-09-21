using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
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
/// A blank id on save mints a brand-new cycle (nobodies-collective/Humans#1631),
/// and a save that deactivates a row leaves it reachable by id. There is no GET to
/// re-render, so every failure branch is post-redirect-get with the rule in the flash.
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
    public async Task Index_Post_RedirectsBackToTheRowItJustSaved()
    {
        // Deactivating takes the row off the tab's default; without the id it would be
        // stranded and the next save would mint another one.
        var id = Guid.NewGuid();

        var result = await BuildSut().Index(MakeForm(id, isActive: false), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/Settings?event={id}#event");
        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Id == id && s.Status == EventSettingsStatus.Inactive),
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_Post_WithoutAnId_MintsANewCycle()
    {
        var sut = BuildSut();

        var result = await sut.Index(MakeForm(id: null, isActive: false), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>();
        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Is<EventSettingsInfo>(s => s.Id != Guid.Empty), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_Post_ActivationConflict_FlashesTheConflictAndRedirectsToTheTab()
    {
        // Checking Active on an inactive row while another cycle is Active is an ordinary
        // operator conflict. The service says so by throwing; there is no GET to re-render,
        // so the message travels as a flash on the way back to the tab.
        const string Conflict =
            "Only one event settings row can be Active at a time — deactivate the current one first.";
        _settings.SaveEventSettingsAsync(Arg.Any<EventSettingsInfo>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(Conflict)));
        var sut = BuildSut();
        var id = Guid.NewGuid();

        var result = await sut.Index(MakeForm(id, isActive: true), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/Settings?event={id}#event");
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be(Conflict);
    }

    [HumansFact]
    public async Task Index_Post_InvalidModel_FlashesTheFailingRuleAndRedirectsToTheTab()
    {
        // The form is only ever rendered by the /Settings#event tab, so a validation
        // failure has to name the broken rule in the flash or the operator sees nothing.
        var sut = BuildSut();
        sut.ModelState.AddModelError(nameof(EventSettingsViewModel.TimeZoneId), "Timezone is required.");

        var result = await sut.Index(MakeForm(id: null, isActive: false), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event");
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be("Timezone is required.");
        await _settings.DidNotReceiveWithAnyArgs().SaveEventSettingsAsync(default!, default, default);
    }

    [HumansFact]
    public async Task Index_Post_WithNoIdentifiableActor_ChallengesAndSavesNothing()
    {
        // Every save is audited by actor, so a principal carrying no usable id is
        // challenged rather than saved anonymously.
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var sut = new SettingsAdminController(_settings, _users)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };

        var result = await sut.Index(
            MakeForm(Guid.NewGuid(), isActive: false), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ChallengeResult>();
        await _settings.DidNotReceiveWithAnyArgs().SaveEventSettingsAsync(default!, default, default);
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
