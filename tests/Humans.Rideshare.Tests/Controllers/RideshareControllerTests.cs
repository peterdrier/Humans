using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.Primitives;
using Xunit;
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
using Microsoft.Extensions.Logging;
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
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IRideshareService _rideshare = Substitute.For<IRideshareService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IStringLocalizer<RideshareResource> _localizer = Substitute.For<IStringLocalizer<RideshareResource>>();
    private static readonly Guid EventId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly ILogger<RideshareController> _logger = Substitute.For<ILogger<RideshareController>>();

    public RideshareControllerTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), "L:" + ci.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci => new LocalizedString(ci.Arg<string>(), "L:" + ci.Arg<string>()));
        _users.GetUserInfoAsync(_me, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(User(_me)));
        _rideshare.GetActiveEventIdAsync(Arg.Any<CancellationToken>()).Returns(EventId);
    }

    [HumansFact]
    public async Task Anonymous_IsChallenged()
    {
        var result = await BuildController(signedIn: false).Accept(Guid.NewGuid(), Ct);

        result.Should().BeOfType<ChallengeResult>();
        await _rideshare.DidNotReceiveWithAnyArgs().AcceptInterestAsync(default, default);
    }

    [HumansFact]
    public async Task Accept_OnSuccess_ToastsAndRedirectsToMine()
    {
        _rideshare.AcceptInterestAsync(Arg.Any<Guid>(), _me, Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult());
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
        AssertExpectedWarnings(2);
    }

    [HumansFact]
    public async Task Accept_MapsARule_ToAnErrorToastOnMine()
    {
        _rideshare.AcceptInterestAsync(Arg.Any<Guid>(), _me, Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult(Refusal: new("Rideshare_Error_NotEnoughSeats")));
        var controller = BuildController();

        var result = await controller.Accept(Guid.NewGuid(), Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Mine");
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("L:Rideshare_Error_NotEnoughSeats");
        controller.TempData[TempDataKeys.SuccessMessage].Should().BeNull();
        AssertExpectedWarnings(1);
    }

    [HumansTheory]
    [InlineData(nameof(RideshareSettingsViewModel.DestinationLatitude))]
    [InlineData(nameof(RideshareSettingsViewModel.DestinationLongitude))]
    [InlineData(null)]
    public async Task AdminSettings_RequirePostedCoordinatesButAcceptExplicitZero(string? omitted)
    {
        var member = BuildController();
        var controller = new RideshareAdminController(
            _rideshare, _users, _localizer, Substitute.For<IClock>(),
            Substitute.For<ILogger<RideshareAdminController>>())
        {
            ControllerContext = member.ControllerContext,
            TempData = member.TempData,
            Url = member.Url
        };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["EventSettingsId"] = EventId.ToString(),
            ["DestinationLabel"] = "Burn",
            ["DestinationLatitude"] = "0",
            ["DestinationLongitude"] = "0",
            ["InboundWindowStart"] = "2026-07-01",
            ["InboundWindowEnd"] = "2026-07-03",
            ["OutboundWindowStart"] = "2026-07-10",
            ["OutboundWindowEnd"] = "2026-07-12"
        };
        if (omitted is not null)
            fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(typeof(RideshareSettingsViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(RideshareSettingsViewModel)
        });
        var context = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var model = (RideshareSettingsViewModel)context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);
        _rideshare.GetSnapshotAsync(EventId, Ct).Returns(new RideshareSnapshot(EventId, "Test Burn", null, [], [], []));
        _rideshare.SaveSettingsAsync(EventId, Arg.Any<SettingsSave>(), _me, Ct).Returns(new RideshareMutationResult());

        var result = await controller.Index(model, Ct);

        if (omitted is not null)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            await _rideshare.DidNotReceive().SaveSettingsAsync(
                Arg.Any<Guid?>(), Arg.Any<SettingsSave>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            controller.TempData.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await _rideshare.Received(1).SaveSettingsAsync(EventId, Arg.Is<SettingsSave>(
                save => save.DestinationLatitude == 0 && save.DestinationLongitude == 0), _me, Ct);
        }
    }

    [HumansTheory]
    [InlineData(true, "Direction", true, false)]
    [InlineData(true, "VehicleType", true, false)]
    [InlineData(true, "LuggageCapacity", true, false)]
    [InlineData(true, "CostSharing", true, false)]
    [InlineData(true, "ExpectedDurationDays", true, false)]
    [InlineData(true, "SeatsOffered", true, false)]
    [InlineData(false, "Direction", true, false)]
    [InlineData(false, "LuggageLoad", true, false)]
    [InlineData(false, "PartySize", true, false)]
    [InlineData(true, null, true, false)]
    [InlineData(false, null, true, false)]
    [InlineData(true, "CostSharing", false, false)]
    [InlineData(false, "LuggageLoad", false, false)]
    [InlineData(true, "WillingToDetour", true, false)]
    [InlineData(false, "CanContributeToFuel", true, false)]
    [InlineData(true, null, true, true)]
    [InlineData(false, null, true, true)]
    public async Task PostingForms_require_explicit_choices_and_counts_before_saving(bool offer, string? omitted, bool signedIn, bool choice)
    {
        var controller = BuildController(signedIn);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Id"] = Guid.NewGuid().ToString(),
            ["Direction"] = "0",
            ["MemberPlaceLabel"] = "Paris",
            ["PickupPlaceLabel"] = "Paris",
            ["DepartureDate"] = "2026-07-03",
            ["DesiredDate"] = "2026-07-03",
            ["ExpectedDurationDays"] = "1",
            ["SeatsOffered"] = "1",
            ["PartySize"] = "1",
            ["VehicleType"] = "0",
            ["LuggageCapacity"] = "0",
            ["LuggageLoad"] = "0",
            ["CostSharing"] = "0",
            ["WillingToDetour"] = choice ? "true" : "false",
            ["CanContributeToFuel"] = choice ? "true" : "false"
        };
        if (omitted is not null) fields.Remove(omitted);
        var formType = offer ? typeof(OfferFormViewModel) : typeof(RequestFormViewModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(formType);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = formType
        });
        var context = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var model = context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);
        _rideshare.UpdateOfferAsync(Arg.Any<Guid>(), _me, Arg.Any<TripSave>(), Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult());
        _rideshare.UpdateRequestAsync(Arg.Any<Guid>(), _me, Arg.Any<RequestSave>(), Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult());

        var result = offer
            ? await controller.Offer((OfferFormViewModel)model, Ct)
            : await controller.RideRequest((RequestFormViewModel)model, Ct);

        if (!signedIn)
        {
            result.Should().BeOfType<ChallengeResult>();
            _rideshare.ReceivedCalls().Should().BeEmpty();
        }
        else if (omitted is not null)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            _rideshare.ReceivedCalls().Should().BeEmpty();
            controller.TempData.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            if (offer)
                await _rideshare.Received(1).UpdateOfferAsync(Arg.Any<Guid>(), _me,
                    Arg.Is<TripSave>(save => (int)save.Direction == 0 && (int)save.VehicleType == 0
                        && (int)save.LuggageCapacity == 0 && (int)save.CostSharing == 0
                        && save.ExpectedDurationDays == 1 && save.SeatsOffered == 1 && save.WillingToDetour == choice), Ct);
            else
                await _rideshare.Received(1).UpdateRequestAsync(Arg.Any<Guid>(), _me,
                    Arg.Is<RequestSave>(save => (int)save.Direction == 0 && (int)save.LuggageLoad == 0 && save.PartySize == 1 && save.CanContributeToFuel == choice), Ct);
        }
    }

    [HumansFact]
    public async Task Interest_WithoutATrip_SendsTheHumanBackToTheBoard()
    {
        var controller = BuildController();

        var result = await controller.Interest(Guid.Empty, null, 1, null, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be("L:Rideshare_PickAnOffer");
        await _rideshare.DidNotReceiveWithAnyArgs().ExpressInterestAsync(default, default, default, default, default);
    }

    [HumansTheory]
    [InlineData("", true)]
    [InlineData("2", true)]
    [InlineData("two", false)]
    public async Task Interest_BindsTheRealForm_AClearedSeatsBoxTakesTheDefault(string seats, bool accepted)
    {
        var tripId = Guid.NewGuid();
        var controller = BuildController();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        _rideshare.ExpressInterestAsync(_me, tripId, null, Arg.Any<int>(), null, Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult());
        var fields = new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["tripId"] = tripId.ToString(),
            ["seats"] = seats
        });
        var method = typeof(RideshareController).GetMethod(nameof(RideshareController.Interest))!;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
        {
            var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
            var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                CacheToken = parameter
            });
            var binding = await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
                new FormValueProvider(BindingSource.Form, fields, CultureInfo.InvariantCulture),
                new ControllerParameterDescriptor { Name = parameter.Name!, ParameterType = parameter.ParameterType, ParameterInfo = parameter },
                metadata, value: null);
            arguments[parameter.Name!] = binding.IsModelSet ? binding.Model : null;
        }

        var result = await controller.Interest((Guid)arguments["tripId"]!, (Guid?)arguments["requestId"],
            (int?)arguments["seats"], (string?)arguments["message"], Ct);

        if (accepted)
        {
            result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Mine");
            await _rideshare.Received(1).ExpressInterestAsync(_me, tripId, null,
                seats.Length == 0 ? 0 : int.Parse(seats, CultureInfo.InvariantCulture), null, Ct);
        }
        else
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            await _rideshare.DidNotReceiveWithAnyArgs().ExpressInterestAsync(default, default, default, default, default);
        }
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
        await _rideshare.DidNotReceiveWithAnyArgs().CreateOfferAsync(default, default, default!);
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task OfferPost_ARule_RerendersTheFormWithAModelError(string culture)
    {
        using var scope = new Humans.Base.Extensions.CultureScope(culture);
        const string key = "Rideshare_Error_PlaceNotFound";
        var resources = new System.Resources.ResourceManager(typeof(RideshareResource));
        var translated = string.Format(CultureInfo.CurrentUICulture,
            resources.GetString(key, CultureInfo.CurrentUICulture)!, "Atlantis");
        _localizer[key, Arg.Any<object[]>()].Returns(call => new LocalizedString(key,
            string.Format(CultureInfo.CurrentUICulture,
                resources.GetString(key, CultureInfo.CurrentUICulture)!, call.Arg<object[]>())));
        _rideshare.CreateOfferAsync(_me, EventId, Arg.Any<TripSave>(), Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult(Refusal: new("Rideshare_Error_PlaceNotFound", "Atlantis")));
        var model = new OfferFormViewModel { MemberPlaceLabel = "Atlantis", DepartureDate = "2026-07-03" };
        var controller = BuildController();

        var result = await controller.Offer(model, Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(translated);
        translated.Should().Contain("Atlantis");
        controller.TempData.Should().BeEmpty();
        AssertExpectedWarnings(1);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferPost_MapsMissingAndForbidden_AndLogsWithoutStack(bool forbidden)
    {
        var id = Guid.NewGuid();
        Exception error = forbidden ? new UnauthorizedAccessException("Only the driver can edit this ride")
            : new KeyNotFoundException($"Trip {id} not found");
        _rideshare.UpdateOfferAsync(id, _me, Arg.Any<TripSave>(), Arg.Any<CancellationToken>()).ThrowsAsync(error);
        var model = new OfferFormViewModel { Id = id, MemberPlaceLabel = "Paris", DepartureDate = "2026-07-03" };
        var controller = BuildController();

        var result = await controller.Offer(model, Ct);

        if (forbidden) result.Should().BeOfType<ForbidResult>();
        else result.Should().BeOfType<NotFoundResult>();
        controller.TempData.Should().BeEmpty();
        AssertExpectedWarnings(1);
        _logger.ReceivedCalls().Single().GetArguments()[2]!.ToString().Should().Contain(error.Message);
    }

    [HumansFact]
    public async Task AdminSettings_RuleRerendersForm_AndLogsEventAndKeyWithoutStack()
    {
        const string key = "Rideshare_Error_WindowOrder";
        _rideshare.SaveSettingsAsync(EventId, Arg.Any<SettingsSave>(), _me, Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult(Refusal: new(key)));
        _rideshare.GetSnapshotAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new RideshareSnapshot(EventId, "Test Burn", null, [], [], []));
        var logger = Substitute.For<ILogger<RideshareAdminController>>();
        var memberController = BuildController();
        var controller = new RideshareAdminController(_rideshare, _users, _localizer, Substitute.For<IClock>(), logger)
        {
            ControllerContext = memberController.ControllerContext,
            TempData = memberController.TempData
        };
        var model = new RideshareSettingsViewModel
        {
            EventSettingsId = EventId,
            DestinationLabel = "Burn",
            InboundWindowStart = "2026-07-03",
            InboundWindowEnd = "2026-07-01",
            OutboundWindowStart = "2026-07-10",
            OutboundWindowEnd = "2026-07-11"
        };

        (await controller.Index(model, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be("L:" + key);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(key).And.Contain(EventId.ToString());
    }

    private void AssertExpectedWarnings(int count)
    {
        var logs = _logger.ReceivedCalls().Where(call =>
            string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).ToList();
        logs.Should().HaveCount(count);
        foreach (var log in logs)
        {
            var args = log.GetArguments();
            args[0].Should().Be(LogLevel.Warning);
            args[3].Should().BeNull();
            args[2]!.ToString().Should().Contain("Test");
        }
    }

    [HumansFact]
    public async Task OfferPost_WithAnId_Updates_AndRedirectsToMine()
    {
        _rideshare.UpdateOfferAsync(Arg.Any<Guid>(), _me, Arg.Any<TripSave>(), Arg.Any<CancellationToken>())
            .Returns(new RideshareMutationResult());
        var id = Guid.NewGuid();
        var model = new OfferFormViewModel { Id = id, MemberPlaceLabel = "Paris", DepartureDate = "2026-07-03" };
        var controller = BuildController();

        var result = await controller.Offer(model, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Mine");
        await _rideshare.Received(1).UpdateOfferAsync(id, _me, Arg.Is<TripSave>(s => s.MemberPlaceLabel == "Paris"), Arg.Any<CancellationToken>());
        await _rideshare.DidNotReceiveWithAnyArgs().CreateOfferAsync(default, default, default!);
        controller.TempData[TempDataKeys.SuccessMessage].Should().Be("L:Rideshare_OfferSaved");
    }

    [HumansTheory]
    [InlineData("toast")]
    [InlineData("form")]
    [InlineData("admin")]
    public async Task DependencyFaultsPropagateWithoutRefusalFeedback(string path)
    {
        var fault = new InvalidOperationException("database diagnostic");
        _rideshare.AcceptInterestAsync(Arg.Any<Guid>(), _me, Arg.Any<CancellationToken>()).ThrowsAsync(fault);
        _rideshare.CreateOfferAsync(_me, EventId, Arg.Any<TripSave>(), Arg.Any<CancellationToken>()).ThrowsAsync(fault);
        _rideshare.SaveSettingsAsync(EventId, Arg.Any<SettingsSave>(), _me, Arg.Any<CancellationToken>()).ThrowsAsync(fault);
        var member = BuildController();
        var admin = new RideshareAdminController(_rideshare, _users, _localizer, Substitute.For<IClock>(),
            Substitute.For<ILogger<RideshareAdminController>>())
        {
            ControllerContext = member.ControllerContext,
            TempData = member.TempData
        };
        Func<Task<IActionResult>> action = path switch
        {
            "toast" => () => member.Accept(Guid.NewGuid(), Ct),
            "form" => () => member.Offer(new OfferFormViewModel
            { MemberPlaceLabel = "Paris", DepartureDate = "2026-07-03" }, Ct),
            "admin" => () => admin.Index(new RideshareSettingsViewModel
            {
                EventSettingsId = EventId,
                DestinationLabel = "Burn",
                InboundWindowStart = "2026-07-01",
                InboundWindowEnd = "2026-07-03",
                OutboundWindowStart = "2026-07-10",
                OutboundWindowEnd = "2026-07-12"
            }, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        member.TempData.Should().BeEmpty();
        member.ModelState.IsValid.Should().BeTrue();
        admin.ModelState.IsValid.Should().BeTrue();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    private RideshareController BuildController(bool signedIn = true)
    {
        var controller = new RideshareController(
            _rideshare, _users, _localizer, Substitute.For<IClock>(), _logger);

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
        null, null, null, null, null, null,
        null, null, null, [], [], [], null, []);
}
