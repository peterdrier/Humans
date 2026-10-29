using System.Security.Claims;
using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Settings.Contracts;
using Humans.Settings.Controllers;
using Humans.Settings.Models;
using Humans.Settings.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NodaTime;
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

/// <summary>
/// A blank id on save mints a brand-new cycle,
/// and a save that deactivates a row leaves it reachable by id. There is no GET to
/// re-render, so every failure branch is post-redirect-get with the rule in the flash.
/// </summary>
public sealed class SettingsAdminControllerTests
{
    private readonly ISettingsWriteService _settings = Substitute.For<ISettingsWriteService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    public SettingsAdminControllerTests()
    {
        _settings.SaveEventSettingsAsync(Arg.Any<EventSettingsInfo>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new EventSettingsSaveResult(true));
    }

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
        GateOpeningDate = "2026-07-09",
        IsActive = isActive,
    };

    [HumansTheory]
    [Xunit.InlineData("Id", false)]
    [Xunit.InlineData("BuildStartOffset", false)]
    [Xunit.InlineData("EventEndOffset", false)]
    [Xunit.InlineData("StrikeEndOffset", false)]
    [Xunit.InlineData("FirstCrewStartOffset", false)]
    [Xunit.InlineData("SetupWeekStartOffset", false)]
    [Xunit.InlineData("PreEventWeekStartOffset", false)]
    [Xunit.InlineData("FinishingWeekendStartOffset", false)]
    [Xunit.InlineData("EarlyEntryCapacityJson", false)]
    [Xunit.InlineData("BarriosEarlyEntryAllocationJson", false)]
    [Xunit.InlineData("EarlyEntryClose", false)]
    [Xunit.InlineData("EarlyEntryStartOffset", false)]
    [Xunit.InlineData("IsActive", false)]
    [Xunit.InlineData("IsOffSeason", false)]
    [Xunit.InlineData("", false)]
    [Xunit.InlineData("", true)]
    public async Task Event_form_requires_posted_values_before_replacing_cycle_settings(string omitted, bool blankOptional)
    {
        var id = Guid.NewGuid();
        var controller = BuildSut();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = blankOptional ? "" : id.ToString(),
            ["EventName"] = "Nowhere 2026",
            ["GateOpeningDate"] = "2026-07-09",
            ["BuildStartOffset"] = "-30",
            ["FirstCrewStartOffset"] = "-24",
            ["SetupWeekStartOffset"] = "-15",
            ["PreEventWeekStartOffset"] = "-8",
            ["FinishingWeekendStartOffset"] = "-3",
            ["EventEndOffset"] = blankOptional ? "0" : "7",
            ["StrikeEndOffset"] = blankOptional ? "0" : "10",
            ["EarlyEntryCapacityJson"] = blankOptional ? "{}" : "{\"-7\":50}",
            ["BarriosEarlyEntryAllocationJson"] = blankOptional ? "" : "{\"-7\":40}",
            ["EarlyEntryClose"] = blankOptional ? "" : "2026-07-01T12:00:00Z",
            ["EarlyEntryStartOffset"] = blankOptional ? "" : "-7",
            ["IsActive"] = blankOptional ? "false" : "true",
            ["IsOffSeason"] = blankOptional ? "false" : "true"
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(EventSettingsViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(EventSettingsViewModel)
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (EventSettingsViewModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, binding.ValidationState, "", model);

        (await controller.Index(model, TestContext.Current.CancellationToken)).Should().BeOfType<RedirectResult>();

        if (omitted.Length > 0)
        {
            _settings.ReceivedCalls().Should().BeEmpty();
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            controller.TempData.Should().ContainKey(TempDataKeys.ErrorMessage);
        }
        else
        {
            controller.ModelState.IsValid.Should().BeTrue();
            var saved = Xunit.Assert.IsType<EventSettingsInfo>(_settings.ReceivedCalls().Should().ContainSingle().Subject.GetArguments()[0]);
            saved.Id.Should().NotBe(Guid.Empty);
            if (!blankOptional) saved.Id.Should().Be(id);
            saved.BuildStartOffset.Should().Be(-30);
            saved.FirstCrewStartOffset.Should().Be(-24);
            saved.SetupWeekStartOffset.Should().Be(-15);
            saved.PreEventWeekStartOffset.Should().Be(-8);
            saved.FinishingWeekendStartOffset.Should().Be(-3);
            saved.EventEndOffset.Should().Be(blankOptional ? 0 : 7);
            saved.StrikeEndOffset.Should().Be(blankOptional ? 0 : 10);
            saved.EarlyEntryCapacity.Should().BeEquivalentTo(blankOptional ? new Dictionary<int, int>() : new() { [-7] = 50 });
            saved.BarriosEarlyEntryAllocation.Should().BeEquivalentTo(blankOptional ? null : new Dictionary<int, int> { [-7] = 40 });
            saved.EarlyEntryClose.Should().Be(blankOptional ? null : Instant.FromUtc(2026, 7, 1, 12, 0));
            saved.EarlyEntryStartOffset.Should().Be(blankOptional ? null : -7);
            saved.Status.Should().Be(blankOptional ? EventSettingsStatus.Inactive : EventSettingsStatus.Active);
            saved.IsOffSeason.Should().Be(!blankOptional);
        }
    }

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
        const string Conflict =
            "Only one event settings row can be Active at a time — deactivate the current one first.";
        _settings.SaveEventSettingsAsync(Arg.Any<EventSettingsInfo>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new EventSettingsSaveResult(false, Conflict));
        var sut = BuildSut();
        var id = Guid.NewGuid();

        var result = await sut.Index(MakeForm(id, isActive: true), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/Settings?event={id}#event");
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be(Conflict);
    }

    [HumansFact]
    public async Task Index_Post_DependencyFailurePropagatesWithoutAnOperatorConflictFlash()
    {
        var failure = new InvalidOperationException("Storage operation failed");
        _settings.SaveEventSettingsAsync(Arg.Any<EventSettingsInfo>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EventSettingsSaveResult>(failure));
        var sut = BuildSut();

        Func<Task> act = async () => await sut.Index(MakeForm(Guid.NewGuid(), isActive: true), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        sut.TempData.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Index_Post_InvalidModel_FlashesTheFailingRuleAndRedirectsToTheTab()
    {
        // The form is only ever rendered by the /Settings#event tab, so a validation
        // failure has to name the broken rule in the flash or the operator sees nothing.
        var sut = BuildSut();
        sut.ModelState.AddModelError(nameof(EventSettingsViewModel.EventName), "Event name is required.");

        var result = await sut.Index(MakeForm(id: null, isActive: false), TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event");
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be("Event name is required.");
        await _settings.DidNotReceiveWithAnyArgs().SaveEventSettingsAsync(default!, default);
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
        await _settings.DidNotReceiveWithAnyArgs().SaveEventSettingsAsync(default!, default);
    }

    [HumansFact]
    public async Task Index_Post_SavesWithTheAuthenticatedActor()
    {
        // The controller resolves the actor from the claims principal, with no Users lookup.
        var sut = BuildSut();
        var actorId = Guid.Parse(((ClaimsIdentity)sut.HttpContext.User.Identity!)
            .FindFirst(ClaimTypes.NameIdentifier)!.Value);

        await sut.Index(MakeForm(Guid.NewGuid(), isActive: false), TestContext.Current.CancellationToken);

        await _settings.Received(1).SaveEventSettingsAsync(
            Arg.Any<EventSettingsInfo>(), actorId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Activate_Post_RedirectsToTheEventItMadeActive()
    {
        var id = Guid.NewGuid();
        _settings.ActivateEventSettingsAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new EventSettingsSaveResult(true));

        var result = await BuildSut().Activate(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/Settings?event={id}#event");
    }

    [HumansFact]
    public async Task Activate_Post_Refusal_FlashesTheReasonAndRedirectsToTheTab()
    {
        var id = Guid.NewGuid();
        _settings.ActivateEventSettingsAsync(id, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new EventSettingsSaveResult(false, "That event no longer exists."));
        var sut = BuildSut();

        var result = await sut.Activate(id, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event");
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be("That event no longer exists.");
    }

    [HumansFact]
    public async Task TimeZone_Post_SavesAndRedirectsToTheTab()
    {
        _settings.SaveTimeZoneAsync("Atlantic/Canary", Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new EventSettingsSaveResult(true));
        var sut = BuildSut();

        var result = await sut.TimeZone("Atlantic/Canary", TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event");
        sut.TempData.Should().ContainKey(TempDataKeys.SuccessMessage);
    }

    [HumansFact]
    public async Task TimeZone_Post_Refusal_FlashesTheReason()
    {
        _settings.SaveTimeZoneAsync("Mars/Olympus", Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new EventSettingsSaveResult(false, "Invalid IANA timezone ID."));
        var sut = BuildSut();

        var result = await sut.TimeZone("Mars/Olympus", TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#event");
        sut.TempData[TempDataKeys.ErrorMessage].Should().Be("Invalid IANA timezone ID.");
    }
}
