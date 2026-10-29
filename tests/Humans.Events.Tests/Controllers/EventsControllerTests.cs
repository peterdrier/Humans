using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Humans.Base;
using Humans.Base.Extensions;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Base.Constants;
using Humans.Events.Contracts;
using Humans.Events.Controllers;
using Humans.Events.Domain;
using Humans.Events.Models;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;

namespace Humans.Events.Tests.Controllers;

/// <summary>
/// Authorization coverage for the individual-event edit route
/// (<c>GET/POST /Events/Submit/{eventId}/Edit</c>). The "Edit event" link in
/// moderation lifecycle emails points here. It must serve the submitter and
/// Events admins, return 403 (not 404) for any other signed-in user, and 404
/// only when the event genuinely doesn't exist on this route.
/// </summary>
public class EventsControllerTests
{
    private readonly ILogger<EventsController> _logger = Substitute.For<ILogger<EventsController>>();
    private readonly IEventManagement _guide = Substitute.For<IEventManagement>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly IAuthorizationService _authz = Substitute.For<IAuthorizationService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<EventsResource> _localizer = Substitute.For<IStringLocalizer<EventsResource>>();

    public EventsControllerTests()
    {
        _guide.SubmitEventAsync(Arg.Any<Event>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new EventSaveResult());
        _guide.UpdateAndResubmitAsync(Arg.Any<Guid>(), Arg.Any<EventEdit>(), Arg.Any<CancellationToken>()).Returns(new EventSaveResult());
    }

    [HumansTheory]
    [Xunit.InlineData("SubmissionOpenAt", "0")]
    [Xunit.InlineData("SubmissionCloseAt", "0")]
    [Xunit.InlineData("GuidePublishAt", "0")]
    [Xunit.InlineData("MaxPrintSlots", "0")]
    [Xunit.InlineData("", "0")]
    [Xunit.InlineData("", "10000")]
    public async Task Guide_settings_require_posted_window_dates_and_print_limit(string omitted, string printLimit)
    {
        var burn = MakeBurnSettings();
        _guide.GetEventSettingsByIdAsync(burn.Id, Arg.Any<CancellationToken>()).Returns(burn);
        _guide.GetEventSettingsOptionsAsync(Arg.Any<CancellationToken>()).Returns([burn]);
        _guide.SaveGuideSettingsAsync(null, burn.Id, default, default, default, 0).ReturnsForAnyArgs(true);
        var controller = new EventsAdminController(_guide,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventsAdminController>.Instance, _users)
        {
            ControllerContext = BuildController(Guid.NewGuid()).ControllerContext
        };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        controller.Url = Substitute.For<IUrlHelper>();
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["EventSettingsId"] = burn.Id.ToString(),
            ["MaxPrintSlots"] = printLimit,
            ["SubmissionOpenAt"] = "2026-07-01T12:00",
            ["SubmissionCloseAt"] = "2026-07-31T12:00",
            ["GuidePublishAt"] = "2026-08-01T12:00"
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(GuideSettingsViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(GuideSettingsViewModel)
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (GuideSettingsViewModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = await controller.SaveSettings(model);

        if (omitted.Length > 0)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            _guide.ReceivedCalls().Should().NotContain(call =>
                call.GetMethodInfo().Name == nameof(IEventManagement.SaveGuideSettingsAsync));
        }
        else
        {
            result.Should().BeOfType<RedirectResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            await _guide.Received(1).SaveGuideSettingsAsync(null, burn.Id,
                new LocalDateTime(2026, 7, 1, 12, 0), new LocalDateTime(2026, 7, 31, 12, 0),
                new LocalDateTime(2026, 8, 1, 12, 0), int.Parse(printLimit, CultureInfo.InvariantCulture));
        }
    }

    [HumansTheory]
    [Xunit.InlineData("CreateCategory", "IsSensitive", false)]
    [Xunit.InlineData("CreateCategory", "IsActive", false)]
    [Xunit.InlineData("CreateCategory", "DisplayOrder", false)]
    [Xunit.InlineData("CreateCategory", "", false)]
    [Xunit.InlineData("CreateCategory", "", true)]
    [Xunit.InlineData("EditCategory", "IsSensitive", false)]
    [Xunit.InlineData("EditCategory", "IsActive", false)]
    [Xunit.InlineData("EditCategory", "DisplayOrder", false)]
    [Xunit.InlineData("EditCategory", "", false)]
    [Xunit.InlineData("EditCategory", "", true)]
    [Xunit.InlineData("CreateVenue", "IsActive", false)]
    [Xunit.InlineData("CreateVenue", "DisplayOrder", false)]
    [Xunit.InlineData("CreateVenue", "", false)]
    [Xunit.InlineData("CreateVenue", "", true)]
    [Xunit.InlineData("EditVenue", "IsActive", false)]
    [Xunit.InlineData("EditVenue", "DisplayOrder", false)]
    [Xunit.InlineData("EditVenue", "", false)]
    [Xunit.InlineData("EditVenue", "", true)]
    public async Task Taxonomy_forms_require_explicit_metadata_before_saving(string action, string omitted, bool choice)
    {
        var id = Guid.NewGuid();
        _guide.GetCategoryAsync(id, Arg.Any<CancellationToken>())
            .Returns(new EventCategoryView(id, "Old category", "old-category", true, 7, false));
        _guide.GetVenueAsync(id, Arg.Any<CancellationToken>())
            .Returns(new EventVenueView(id, "Old venue", null, null, 7, false));
        var controller = new EventsAdminController(_guide,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventsAdminController>.Instance, _users)
        {
            ControllerContext = BuildController(Guid.NewGuid()).ControllerContext
        };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        controller.Url = Substitute.For<IUrlHelper>();
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Name"] = "Updated",
            ["Slug"] = "updated-category",
            ["DisplayOrder"] = "0",
            ["IsActive"] = choice ? "true" : "false",
            ["IsSensitive"] = choice ? "true" : "false"
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var type = action.EndsWith("Category", StringComparison.Ordinal)
            ? typeof(EventCategoryFormViewModel) : typeof(GuideVenueFormViewModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(type);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = type
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = action switch
        {
            "CreateCategory" => await controller.CreateCategory((EventCategoryFormViewModel)model),
            "EditCategory" => await controller.EditCategory(id, (EventCategoryFormViewModel)model),
            "CreateVenue" => await controller.CreateVenue((GuideVenueFormViewModel)model),
            _ => await controller.EditVenue(id, (GuideVenueFormViewModel)model)
        };
        var writes = _guide.ReceivedCalls().Where(call => call.GetMethodInfo().Name is
            nameof(IEventManagement.CreateCategoryAsync) or nameof(IEventManagement.UpdateCategoryAsync)
            or nameof(IEventManagement.CreateVenueAsync) or nameof(IEventManagement.UpdateVenueAsync)).ToList();
        if (omitted.Length > 0)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            writes.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            var saved = writes.Should().ContainSingle().Which.GetArguments()[0];
            if (saved is EventCategory category)
            {
                category.IsSensitive.Should().Be(choice);
                category.IsActive.Should().Be(choice);
                category.DisplayOrder.Should().Be(0);
            }
            else
            {
                var venue = saved.Should().BeOfType<EventVenue>().Which;
                venue.IsActive.Should().Be(choice);
                venue.DisplayOrder.Should().Be(0);
            }
        }
    }

    [HumansTheory]
    [Xunit.InlineData("Create", "StartTime")]
    [Xunit.InlineData("Create", "DurationMinutes")]
    [Xunit.InlineData("Create", "IsRecurring")]
    [Xunit.InlineData("Create", "IsAllDay")]
    [Xunit.InlineData("Create", "validFalse")]
    [Xunit.InlineData("Create", "validTrue")]
    [Xunit.InlineData("Update", "StartTime")]
    [Xunit.InlineData("Update", "DurationMinutes")]
    [Xunit.InlineData("Update", "IsRecurring")]
    [Xunit.InlineData("Update", "IsAllDay")]
    [Xunit.InlineData("Update", "validFalse")]
    [Xunit.InlineData("Update", "validTrue")]
    [Xunit.InlineData("BarrioCreate", "StartTime")]
    [Xunit.InlineData("BarrioCreate", "DurationMinutes")]
    [Xunit.InlineData("BarrioCreate", "IsRecurring")]
    [Xunit.InlineData("BarrioCreate", "validFalse")]
    [Xunit.InlineData("BarrioCreate", "validTrue")]
    [Xunit.InlineData("BarrioUpdate", "StartTime")]
    [Xunit.InlineData("BarrioUpdate", "DurationMinutes")]
    [Xunit.InlineData("BarrioUpdate", "IsRecurring")]
    [Xunit.InlineData("BarrioUpdate", "validFalse")]
    [Xunit.InlineData("BarrioUpdate", "validTrue")]
    [Xunit.InlineData("AdminIndividual", "StartTime")]
    [Xunit.InlineData("AdminIndividual", "DurationMinutes")]
    [Xunit.InlineData("AdminIndividual", "IsRecurring")]
    [Xunit.InlineData("AdminIndividual", "IsAllDay")]
    [Xunit.InlineData("AdminIndividual", "validFalse")]
    [Xunit.InlineData("AdminIndividual", "validTrue")]
    [Xunit.InlineData("AdminCamp", "StartTime")]
    [Xunit.InlineData("AdminCamp", "DurationMinutes")]
    [Xunit.InlineData("AdminCamp", "IsRecurring")]
    [Xunit.InlineData("AdminCamp", "PriorityRank")]
    [Xunit.InlineData("AdminCamp", "validBlankRank")]
    [Xunit.InlineData("AdminCamp", "validFalse")]
    [Xunit.InlineData("AdminCamp", "validTrue")]
    public async Task Authoring_requires_explicit_schedule_fields_before_changing_events(string route, string scenario)
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var isModerator = route.StartsWith("Admin", StringComparison.Ordinal);
        var isCamp = route.StartsWith("Barrio", StringComparison.Ordinal) || string.Equals(route, "AdminCamp", StringComparison.Ordinal);
        var valid = scenario.StartsWith("valid", StringComparison.Ordinal);
        var checkedChoices = string.Equals(scenario, "validTrue", StringComparison.Ordinal);
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, "camp", 2026, null, "Camp", "", "en", [],
            default, default, default, default, 1, null, null, null, 0, null, null)
        { LeadUserIds = [userId] };
        var camp = new CampInfo(campId, "camp", "camp@example.org", "", false, 0, [season]);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        StubEditableGuideSettings();
        var existing = MakeEvent(isCamp ? campId : null, userId,
            isModerator ? EventStatus.Approved : EventStatus.ResubmitRequested) with
        {
            PriorityRank = isCamp ? 2 : null,
            DurationMinutes = 180,
            IsRecurring = true,
            RecurrenceDays = "0,1"
        };
        _guide.GetEventForModerationAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _guide.GetCampEventAsync(existing.Id, campId, Arg.Any<CancellationToken>()).Returns(existing);
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var author = BuildController(userId);
        Controller controller = isModerator
            ? new EventsModerationController(_guide, _users, _camps,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EventsModerationController>.Instance)
            { ControllerContext = author.ControllerContext }
            : author;
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        controller.Url.Action(Arg.Any<Microsoft.AspNetCore.Mvc.Routing.UrlActionContext>()).Returns("/Events");
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Title"] = "Changed",
            ["Description"] = "Changed description",
            ["CategoryId"] = existing.CategoryId.ToString(),
            ["VenueId"] = Guid.NewGuid().ToString(),
            ["StartDate"] = "2026-08-01",
            ["StartTime"] = "00:00",
            ["DurationMinutes"] = "60",
            ["IsRecurring"] = checkedChoices ? "true" : "false",
            ["RecurrenceDays"] = "0,1",
            ["IsAllDay"] = checkedChoices && !isCamp ? "true" : "false",
            ["PriorityRank"] = "1"
        };
        fields.Remove(isCamp ? "IsAllDay" : "PriorityRank");
        if (string.Equals(scenario, "validBlankRank", StringComparison.Ordinal)) fields["PriorityRank"] = "";
        if (!valid) fields.Remove(scenario);
        var type = isModerator ? typeof(AdminEventFormViewModel)
            : isCamp ? typeof(CampEventFormViewModel) : typeof(IndividualEventFormViewModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(type);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = type
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = route switch
        {
            "Create" => await author.Create((IndividualEventFormViewModel)model),
            "Update" => await author.Update(existing.Id, (IndividualEventFormViewModel)model),
            "BarrioCreate" => await author.BarrioCreate(camp.Slug, (CampEventFormViewModel)model),
            "BarrioUpdate" => await author.BarrioUpdate(camp.Slug, existing.Id, (CampEventFormViewModel)model),
            _ => await ((EventsModerationController)controller).Update(existing.Id, (AdminEventFormViewModel)model)
        };
        var writes = _guide.ReceivedCalls().Where(call => call.GetMethodInfo().Name is
            nameof(IEventManagement.SubmitEventAsync) or nameof(IEventManagement.UpdateAndResubmitAsync) or nameof(IEventManagement.AdminUpdateAsync)).ToList();
        if (!valid)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[scenario]!.Errors.Should().ContainSingle();
            writes.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            // Create routes persist a new entity; edit routes send an EventEdit by id.
            var args = writes.Should().ContainSingle().Which.GetArguments();
            var saved = args[0] is Event created
                ? new EventEdit(created.CategoryId, created.GuideSharedVenueId, created.Title, created.Description,
                    created.StartAt, created.DurationMinutes, created.LocationNote, created.Host,
                    created.IsRecurring, created.RecurrenceDays, created.PriorityRank)
                : (EventEdit)args[1]!;
            if (args[0] is Guid editedId)
                editedId.Should().Be(existing.Id);
            saved.IsRecurring.Should().Be(checkedChoices);
            if (isCamp)
            {
                int? expectedRank = string.Equals(scenario, "validBlankRank", StringComparison.Ordinal) ? null : 1;
                saved.PriorityRank.Should().Be(expectedRank);
            }
            saved.DurationMinutes.Should().Be(checkedChoices && !isCamp ? 1440 : 60);
            saved.StartAt.Should().Be(new LocalDate(2026, 8, 1)
                .AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant());
        }
    }

    [HumansTheory]
    [Xunit.InlineData("Create")]
    [Xunit.InlineData("Update")]
    [Xunit.InlineData("BarrioCreate")]
    [Xunit.InlineData("BarrioUpdate")]
    public async Task Authoring_RejectsRecurrenceOutsideTheConfiguredEvent(string route)
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, "camp", 2026, null, "Camp", "", "en", [],
            default, default, default, default, 1, null, null, null, 0, null, null)
        { LeadUserIds = [userId] };
        var camp = new CampInfo(campId, "camp", "camp@example.org", "", false, 0, [season]);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        StubEditableGuideSettings();
        var guideEvent = MakeEvent(route.StartsWith("Barrio", StringComparison.Ordinal) ? campId : null,
            userId, EventStatus.ResubmitRequested);
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _guide.GetCampEventAsync(guideEvent.Id, campId, Arg.Any<CancellationToken>()).Returns(guideEvent);
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = BuildController(userId);
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        controller.Url.Action(Arg.Any<Microsoft.AspNetCore.Mvc.Routing.UrlActionContext>()).Returns("/Events");
        var individual = new IndividualEventFormViewModel
        {
            Title = "Changed",
            StartDate = new DateTime(2026, 8, 1),
            IsRecurring = true,
            RecurrenceDays = "0,3"
        };
        var barrio = new CampEventFormViewModel
        {
            Title = "Changed",
            StartDate = new DateTime(2026, 8, 1),
            IsRecurring = true,
            RecurrenceDays = "0,3"
        };

        var result = route switch
        {
            "Create" => await controller.Create(individual),
            "Update" => await controller.Update(guideEvent.Id, individual),
            "BarrioCreate" => await controller.BarrioCreate(camp.Slug, barrio),
            _ => await controller.BarrioUpdate(camp.Slug, guideEvent.Id, barrio)
        };

        result.Should().BeOfType<ViewResult>();
        controller.ModelState[nameof(individual.RecurrenceDays)]!.Errors.Should().ContainSingle();
        _guide.ReceivedCalls().Should().NotContain(call =>
            call.GetMethodInfo().Name == nameof(IEventManagement.SubmitEventAsync)
            || call.GetMethodInfo().Name == nameof(IEventManagement.UpdateAndResubmitAsync));
    }

    [HumansTheory]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Guide")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Burn")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Submissions")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "CampSettings")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "Camps")]
    [Xunit.InlineData(nameof(EventsController.MySubmissions), "CampSummary")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Categories")]
    [Xunit.InlineData(nameof(EventsController.Submit), "Venues")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Event")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Categories")]
    [Xunit.InlineData(nameof(EventsController.Edit), "Venues")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Favourites")]
    [Xunit.InlineData(nameof(EventsController.Schedule), "Camps")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Guide")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Burn")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Excluded")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Favourites")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Approved")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Camps")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Submitter")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Categories")]
    [Xunit.InlineData(nameof(EventsController.Browse), "Venues")]
    [Xunit.InlineData(nameof(EventsController.BarrioSubmit), "Camp")]
    [Xunit.InlineData(nameof(EventsController.BarrioSubmit), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.BarrioSubmit), "Guide")]
    [Xunit.InlineData(nameof(EventsController.BarrioSubmit), "Burn")]
    [Xunit.InlineData(nameof(EventsController.BarrioSubmit), "Categories")]
    [Xunit.InlineData(nameof(EventsController.BarrioEdit), "Camp")]
    [Xunit.InlineData(nameof(EventsController.BarrioEdit), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.BarrioEdit), "Event")]
    [Xunit.InlineData(nameof(EventsController.BarrioEdit), "Guide")]
    [Xunit.InlineData(nameof(EventsController.BarrioEdit), "Burn")]
    [Xunit.InlineData(nameof(EventsController.BarrioEdit), "Categories")]
    [Xunit.InlineData(nameof(EventsController.BulkUploadTemplate), "Camp")]
    [Xunit.InlineData(nameof(EventsController.BulkUploadTemplate), "Viewer")]
    [Xunit.InlineData(nameof(EventsController.BulkUploadTemplate), "Template")]
    public async Task MemberReadPages_StopLoadingAfterRequestCancellation(string route, string boundary)
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var controller = BuildController(userId);
        controller.HttpContext.RequestAborted = request.Token;
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        var abandon = false;
        async Task<T> ReadAsync<T>(T value, string current, CancellationToken ct)
        {
            if (abandon && string.Equals(current, boundary, StringComparison.Ordinal))
                await request.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return value;
        }
        StubEditableGuideSettings();
        var settings = (await _guide.GetGuideSettingsAsync(Xunit.TestContext.Current.CancellationToken))! with
        {
            SubmissionOpenAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            SubmissionCloseAt = Instant.FromUtc(2026, 6, 1, 0, 0),
        };
        _clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 5, 15, 0, 0));
        var burn = MakeBurnSettings() with { Id = settings.EventSettingsId };
        var guideEvent = MakeEvent(null, userId, EventStatus.ResubmitRequested);
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, "camp", 2026, null, "Camp", "", "en", [],
            default, default, default, default, 1, null, null, null, 0, null, null)
        { LeadUserIds = [userId] };
        var camp = new CampInfo(campId, "camp", "camp@example.org", "", false, 0, [season]);
        var campEvent = MakeEvent(campId, userId, EventStatus.ResubmitRequested);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<CampInfo?>(camp, "Camp", call.Arg<CancellationToken>()));
        _guide.GetCampEventAsync(campEvent.Id, camp.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventInfo?>(campEvent, "Event", call.Arg<CancellationToken>()));
        _guide.BuildBulkUploadTemplateAsync(camp.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<byte[]>([], "Template", call.Arg<CancellationToken>()));
        var submitterId = Guid.NewGuid();
        var approved = new ApprovedEventView(Guid.NewGuid(), null, null, submitterId, Guid.NewGuid(), "music", "Music", false,
            null, "Event", "Description", null, null, Instant.FromUtc(2026, 8, 1, 18, 0), 60, false, null, null,
            Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 1, 0, 0));
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<UserInfo?>(ReadAsync<UserInfo?>(MakeUserInfo(call.Arg<Guid>(), "Human"),
                call.Arg<Guid>() == userId ? "Viewer" : "Submitter", call.Arg<CancellationToken>())));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(ReadAsync<IReadOnlyDictionary<Guid, UserInfo>>(
                call.Arg<IReadOnlyCollection<Guid>>().Distinct().ToDictionary(id => id, id => MakeUserInfo(id, "Human")),
                "Submitter", call.Arg<CancellationToken>())));
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventGuideSettingsView?>(settings, "Guide", call.Arg<CancellationToken>()));
        _guide.GetEventSettingsByIdAsync(settings.EventSettingsId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventSettingsInfo?>(burn, "Burn", call.Arg<CancellationToken>()));
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<EventInfo?>(guideEvent, "Event", call.Arg<CancellationToken>()));
        _guide.GetUserSubmissionsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventInfo>>([], "Submissions", call.Arg<CancellationToken>()));
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync(new CampSettingsInfo(2026, [2026]), "CampSettings", call.Arg<CancellationToken>()));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<CampInfo>>([camp], "Camps", call.Arg<CancellationToken>()));
        _guide.GetCampSubmissionsSummaryAsync(campId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync(new CampSubmissionsSummary(0, 0, 0, []), "CampSummary", call.Arg<CancellationToken>()));
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventCategoryView>>([], "Categories", call.Arg<CancellationToken>()));
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventVenueView>>([], "Venues", call.Arg<CancellationToken>()));
        _guide.GetFavouritesWithEventsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<EventFavouriteInfo>>([], "Favourites", call.Arg<CancellationToken>()));
        _guide.GetExcludedCategorySlugsAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<List<string>>([], "Excluded", call.Arg<CancellationToken>()));
        _guide.GetApprovedEventsAsync(null, null, null, null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
            ReadAsync<IReadOnlyList<ApprovedEventView>>([approved], "Approved", call.Arg<CancellationToken>()));
        Func<Task<IActionResult>> load = route switch
        {
            nameof(EventsController.MySubmissions) => controller.MySubmissions,
            nameof(EventsController.Submit) => controller.Submit,
            nameof(EventsController.Edit) => () => controller.Edit(guideEvent.Id),
            nameof(EventsController.BarrioSubmit) => () => controller.BarrioSubmit(camp.Slug),
            nameof(EventsController.BarrioEdit) => () => controller.BarrioEdit(camp.Slug, campEvent.Id),
            nameof(EventsController.BulkUploadTemplate) => () => controller.BulkUploadTemplate(camp.Slug),
            nameof(EventsController.Schedule) => controller.Schedule,
            _ => () => controller.Browse(null, null, null, null),
        };
        var loaded = await load();
        if (string.Equals(route, nameof(EventsController.BulkUploadTemplate), StringComparison.Ordinal))
            loaded.Should().BeOfType<FileContentResult>();
        else
            loaded.Should().BeOfType<ViewResult>();
        abandon = true;
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public void SubmissionForms_LocalizeValidationMessages(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection();
        registrations.AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var validator = services.GetRequiredService<IObjectModelValidator>();
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var cases = new (object Model, string Field, string Key, object[] Arguments)[]
        {
            (new IndividualEventFormViewModel(), "Title", "Validation_Required", ["Title"]),
            (new IndividualEventFormViewModel(), "Description", "Validation_Required", ["Description"]),
            (new IndividualEventFormViewModel { Title = new string('x', 81) }, "Title", "Validation_MaxLength", ["Title", 80]),
            (new IndividualEventFormViewModel { Description = new string('x', 451) }, "Description", "Validation_MaxLength", ["Description", 450]),
            (new IndividualEventFormViewModel { LocationNote = new string('x', 121) }, "LocationNote", "Validation_MaxLength", ["Location Note", 120]),
            (new IndividualEventFormViewModel { Host = new string('x', 41) }, "Host", "Validation_MaxLength", ["Host", 40]),
            (new IndividualEventFormViewModel { DurationMinutes = 14 }, "DurationMinutes", "Validation_Range", ["Duration (minutes)", 15, 1440]),
            (new CampEventFormViewModel(), "Title", "Validation_Required", ["Title"]),
            (new CampEventFormViewModel(), "Description", "Validation_Required", ["Description"]),
            (new CampEventFormViewModel(), "PriorityRank", "Validation_Required", ["Priority Rank"]),
            (new CampEventFormViewModel { Title = new string('x', 81) }, "Title", "Validation_MaxLength", ["Title", 80]),
            (new CampEventFormViewModel { Description = new string('x', 451) }, "Description", "Validation_MaxLength", ["Description", 450]),
            (new CampEventFormViewModel { LocationNote = new string('x', 121) }, "LocationNote", "Validation_MaxLength", ["Location Note", 120]),
            (new CampEventFormViewModel { Host = new string('x', 41) }, "Host", "Validation_MaxLength", ["Host", 40]),
            (new CampEventFormViewModel { DurationMinutes = 481 }, "DurationMinutes", "Validation_Range", ["Duration (minutes)", 15, 480]),
            (new CampEventFormViewModel { PriorityRank = 101 }, "PriorityRank", "Validation_Range", ["Priority Rank", 1, 100]),
        };
        foreach (var (model, field, key, arguments) in cases)
        {
            var context = new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } };
            validator.Validate(context, null, "", model);
            var expected = localizer[key, arguments];
            expected.ResourceNotFound.Should().BeFalse();
            context.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expected.Value);
        }
    }

    [HumansFact]
    public async Task Edit_NonSubmitterNonAdmin_ReturnsForbid()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: null, EventStatus.ResubmitRequested);
        var controller = BuildController(Guid.NewGuid());

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Edit_AdminNonSubmitter_ReturnsEditForm()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: null, EventStatus.ResubmitRequested);
        StubEditableGuideSettings();
        var controller = BuildController(Guid.NewGuid(), RoleNames.Admin);

        var result = await controller.Edit(eventId);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("IndividualEventForm");
    }

    [HumansFact]
    public async Task Edit_Submitter_ReturnsEditForm()
    {
        var submitterId = Guid.NewGuid();
        var eventId = StubEvent(submitterId, campId: null, EventStatus.ResubmitRequested);
        StubEditableGuideSettings();
        var controller = BuildController(submitterId);

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Edit_MissingEvent_ReturnsNotFound()
    {
        var eventId = Guid.NewGuid();
        _guide.GetEventForModerationAsync(eventId, Arg.Any<CancellationToken>())
            .Returns((EventInfo?)null);
        var controller = BuildController(Guid.NewGuid(), RoleNames.Admin);

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Edit_CampEventOnIndividualRoute_ReturnsNotFound()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: Guid.NewGuid(), EventStatus.ResubmitRequested);
        var controller = BuildController(Guid.NewGuid(), RoleNames.Admin);

        var result = await controller.Edit(eventId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Update_NonSubmitterNonAdmin_ReturnsForbid()
    {
        var eventId = StubEvent(submitterId: Guid.NewGuid(), campId: null, EventStatus.ResubmitRequested);
        var controller = BuildController(Guid.NewGuid());

        var result = await controller.Update(eventId, new IndividualEventFormViewModel());

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task MySubmissions_OrdersCampEventsMostRecentlySubmittedFirst()
    {
        var userId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(), campId, "camp", 2026, null, "Camp", "", "en", [],
            default, default, default, default, 1, null, null, null, 0, null, null)
        {
            LeadUserIds = [userId]
        };
        var camp = new CampInfo(campId, "camp", "camp@example.org", "", false, 0, [season]);
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, [2026]));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns([camp]);
        StubEditableGuideSettings();
        var settings = await _guide.GetGuideSettingsAsync();
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings! with
        {
            SubmissionOpenAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            SubmissionCloseAt = Instant.FromUtc(2026, 6, 1, 0, 0)
        });
        _guide.GetUserSubmissionsAsync(userId, Arg.Any<CancellationToken>()).Returns([]);
        var older = new EventInfo(
            Guid.NewGuid(), campId, null, userId, Guid.NewGuid(), "Music", "music", false,
            null, "Older", "", null, null, Instant.FromUtc(2026, 8, 1, 18, 0), 60, false,
            null, null, EventStatus.Pending, Instant.FromUtc(2026, 5, 1, 12, 0),
            Instant.FromUtc(2026, 5, 1, 12, 0), []);
        var newer = older with { Id = Guid.NewGuid(), Title = "Newer", SubmittedAt = older.SubmittedAt + Duration.FromDays(1) };
        _guide.GetCampSubmissionsSummaryAsync(campId, Arg.Any<CancellationToken>())
            .Returns(new CampSubmissionsSummary(2, 0, 2, [older, newer]));
        var controller = BuildController(userId);
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());

        var result = await controller.MySubmissions();

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<MySubmissionsViewModel>().Subject;
        model.Barrios.Should().ContainSingle().Which.Events.Select(e => e.Title)
            .Should().Equal("Newer", "Older");
    }

    private Guid StubEvent(Guid submitterId, Guid? campId, EventStatus status)
    {
        var guideEvent = MakeEvent(campId, submitterId, status);
        _guide.GetEventForModerationAsync(guideEvent.Id, Arg.Any<CancellationToken>())
            .Returns(guideEvent);
        return guideEvent.Id;
    }

    [HumansTheory]
    [Xunit.InlineData("format")]
    [Xunit.InlineData("quote")]
    [Xunit.InlineData("io")]
    [Xunit.InlineData("dependency-format")]
    [Xunit.InlineData("caller-cancel")]
    [Xunit.InlineData("dependency-cancel")]
    public async Task BulkUpload_SeparatesInputErrorsFromUnexpectedFailures(string failureKind)
    {
        var userId = Guid.NewGuid();
        var camp = new CampInfo(Guid.NewGuid(), "camp", "camp@example.org", "", false, 0, []);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        StubEditableGuideSettings();
        _localizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        _localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        var controller = BuildController(userId);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        controller.ControllerContext.ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(EventsController.BulkUploadImport) };
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        var file = Substitute.For<IFormFile>();
        file.Length.Returns(100);
        using var cancellation = new CancellationTokenSource();
        Exception? failure = null;
        if (string.Equals(failureKind, "caller-cancel", StringComparison.Ordinal))
        {
            await cancellation.CancelAsync();
            controller.HttpContext.RequestAborted = cancellation.Token;
            failure = new OperationCanceledException(cancellation.Token);
        }
        else if (string.Equals(failureKind, "dependency-cancel", StringComparison.Ordinal))
            failure = new OperationCanceledException("Upload storage cancelled");
        else if (string.Equals(failureKind, "io", StringComparison.Ordinal))
            failure = new IOException("Upload stream unavailable");
        else if (string.Equals(failureKind, "dependency-format", StringComparison.Ordinal))
            failure = new FormatException("Secret upload diagnostics");
        if (failure is not null) file.OpenReadStream().Returns(_ => throw failure);
        else
        {
            const string header = "Id,Barrio,Status,Title,Description,Category,Date,StartTime,DurationMinutes,LocationNote,Host,IsRecurring,RecurrenceDays,PriorityRank";
            var csv = string.Equals(failureKind, "quote", StringComparison.Ordinal)
                ? header + "\n,Camp,,Bad\"quote,Description,Workshop,2026-08-01,09:30,60,,,false,,1\n"
                : "MissingRequiredColumns\nrow";
            file.OpenReadStream().Returns(_ => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)));
        }
        var act = () => controller.BulkUploadImport(camp.Slug, file);
        if (string.Equals(failureKind, "caller-cancel", StringComparison.Ordinal))
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
            _logger.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            (await act()).Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(EventsController.MySubmissions));
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(string.Equals(failureKind, "format", StringComparison.Ordinal)
                ? "Events_Upload_ParseErrorDetail" : "Events_Upload_ParseFailed");
            var args = _logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
            args[0].Should().Be(failure is null ? LogLevel.Warning : LogLevel.Error);
            args[3].Should().BeSameAs(failure);
            args[2]!.ToString().Should().Contain(camp.Slug);
        }
        _guide.ReceivedCalls().Should().NotContain(c => string.Equals(c.GetMethodInfo().Name, nameof(IEventManagement.BulkImportAsync), StringComparison.Ordinal));
    }

    [HumansTheory]
    [Xunit.InlineData("individual")]
    [Xunit.InlineData("barrio")]
    [Xunit.InlineData("csv")]
    public async Task SubmissionWindowClosesAfterPreflight_ShowsTheLocalizedRefusal(string route)
    {
        var userId = Guid.NewGuid();
        var camp = new CampInfo(Guid.NewGuid(), "camp", "camp@example.org", "", false, 0, []);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        StubEditableGuideSettings();
        _guide.SubmitEventAsync(Arg.Any<Event>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new EventSaveResult("Events_SubmissionWindowClosed"));
        _guide.BulkImportAsync(camp.Id, userId, Arg.Any<IReadOnlyList<BulkCsvRow>>(),
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<DateTimeZone>(), Arg.Any<CancellationToken>())
            .Returns(new BulkImportResult([], 0, 0, RefusalKey: "Events_SubmissionWindowClosed"));
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = BuildController(userId);
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        const string csv = "Id,Barrio,Status,Title,Description,Category,Date,StartTime,DurationMinutes,LocationNote,Host,IsRecurring,RecurrenceDays,PriorityRank\n,Camp,,Music,Description,Workshop,2026-08-01,09:30,60,,,false,,1\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var file = new FormFile(stream, 0, stream.Length, "file", "events.csv");

        var result = route switch
        {
            "individual" => await controller.Create(new IndividualEventFormViewModel
            {
                Title = "Music",
                Description = "Description",
                VenueId = Guid.NewGuid(),
                CategoryId = Guid.NewGuid(),
                StartDate = new DateTime(2026, 8, 1),
                DurationMinutes = 60
            }),
            "barrio" => await controller.BarrioCreate(camp.Slug, new CampEventFormViewModel
            {
                Title = "Music",
                Description = "Description",
                CategoryId = Guid.NewGuid(),
                StartDate = new DateTime(2026, 8, 1),
                DurationMinutes = 60
            }),
            _ => await controller.BulkUploadImport(camp.Slug, file)
        };

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("MySubmissions");
        controller.TempData["ErrorMessage"].Should().Be("Events_SubmissionWindowClosed");
        controller.TempData.Should().NotContainKey("SuccessMessage");
        controller.TempData.Should().NotContainKey("BulkErrors_camp");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, false, "Events_VenueUnavailable")]
    [Xunit.InlineData(true, false, "Events_VenueUnavailable")]
    public async Task SelectionRefusal_RedisplaysMemberForm_AndPreservesEnteredContent(
        bool edit, bool barrio, string refusalKey = "Events_CategoryUnavailable")
    {
        var field = string.Equals(refusalKey, "Events_VenueUnavailable", StringComparison.Ordinal) ? "VenueId" : "CategoryId";
        var userId = Guid.NewGuid();
        var camp = new CampInfo(Guid.NewGuid(), "camp", "camp@example.org", "", false, 0, []);
        var existing = MakeEvent(barrio ? camp.Id : null, userId, EventStatus.ResubmitRequested);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>()).Returns(camp);
        _authz.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), camp, Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _guide.GetEventForModerationAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _guide.GetCampEventAsync(existing.Id, camp.Id, Arg.Any<CancellationToken>()).Returns(existing);
        StubEditableGuideSettings();
        _guide.SubmitEventAsync(Arg.Any<Event>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new EventSaveResult(refusalKey, field));
        _guide.UpdateAndResubmitAsync(existing.Id, Arg.Any<EventEdit>(), Arg.Any<CancellationToken>()).Returns(new EventSaveResult(refusalKey, field));
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = BuildController(userId);
        controller.TempData = new TempDataDictionary(controller.HttpContext, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        object model;
        IActionResult result;
        if (barrio)
        {
            var input = new CampEventFormViewModel
            {
                Title = "Entered title", Description = "Entered description", CategoryId = Guid.NewGuid(),
                StartDate = new DateTime(2026, 8, 1), DurationMinutes = 60
            };
            model = input;
            result = edit ? await controller.BarrioUpdate(camp.Slug, existing.Id, input) : await controller.BarrioCreate(camp.Slug, input);
            input.Title.Should().Be("Entered title");
            input.Description.Should().Be("Entered description");
            input.CampId.Should().Be(camp.Id);
        }
        else
        {
            var input = new IndividualEventFormViewModel
            {
                Title = "Entered title", Description = "Entered description", CategoryId = Guid.NewGuid(), VenueId = Guid.NewGuid(),
                StartDate = new DateTime(2026, 8, 1), DurationMinutes = 60
            };
            model = input;
            result = edit ? await controller.Update(existing.Id, input) : await controller.Create(input);
            input.Title.Should().Be("Entered title");
            input.Description.Should().Be("Entered description");
        }
        var view = result.Should().BeOfType<ViewResult>().Which;
        view.Model.Should().BeSameAs(model);
        view.ViewName.Should().Be(barrio ? "BarrioEventForm" : "IndividualEventForm");
        controller.ModelState[field]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(refusalKey);
        controller.TempData.Should().NotContainKey("SuccessMessage");
    }

    private void StubEditableGuideSettings()
    {
        _guide.SubmitEventAsync(Arg.Any<Event>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new EventSaveResult());
        var settingsId = Guid.NewGuid();
        _guide.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new EventGuideSettingsView(
                Id: Guid.NewGuid(),
                EventSettingsId: settingsId,
                SubmissionOpenAt: Instant.MinValue,
                SubmissionCloseAt: Instant.MaxValue,
                GuidePublishAt: Instant.MaxValue,
                MaxPrintSlots: 100,
                TimeZoneId: "Europe/Madrid",
                CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0)));
        _guide.GetEventSettingsByIdAsync(settingsId, Arg.Any<CancellationToken>())
            .Returns(MakeBurnSettings());
        _guide.GetActiveCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _guide.GetActiveVenuesAsync(Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private EventsController BuildController(Guid currentUserId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, currentUserId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        _users.GetUserInfoAsync(currentUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(currentUserId, "Current User")));

        return new EventsController(_guide, _users, _camps, _authz, _clock, _logger, _localizer)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static EventSettingsInfo MakeBurnSettings() => new(
        Id: Guid.NewGuid(),
        EventName: "Test Burn",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 8, 1),
        BuildStartOffset: 0,
        EventEndOffset: 2,
        StrikeEndOffset: 0,
        FirstCrewStartOffset: 0,
        SetupWeekStartOffset: 0,
        PreEventWeekStartOffset: 0,
        FinishingWeekendStartOffset: 0,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null);

    private static EventInfo MakeEvent(Guid? campId, Guid submitterId, EventStatus status) => new(
        Guid.NewGuid(), campId, campId == null ? Guid.NewGuid() : null, submitterId, Guid.NewGuid(),
        "Music", "music", false, null, "Test Event", "Description", null, null,
        Instant.FromUtc(2026, 8, 1, 18, 0), 60, false, null, null, status,
        Instant.FromUtc(2026, 7, 1, 0, 0), Instant.FromUtc(2026, 7, 1, 0, 0), []);

    private static UserInfo MakeUserInfo(Guid userId, string burnerName)
    {
        var profile = UserFixtures.Profile(
            burnerName: burnerName,
            firstName: "Test",
            lastName: "User",
            isApproved: true);
        var user = new User
        {
            Id = userId,
            DisplayName = "Test User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            communicationPreferences: []);
    }
}
