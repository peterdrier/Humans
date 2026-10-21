using Humans.Cantina.Contracts;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Globalization;
using Xunit;
using Humans.Base.Extensions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.Controllers;

/// <summary>
/// A department coordinator may delete only rotas and shifts of the department in the URL.
/// The id in the route is untrusted: one that belongs to another team is a 404, and the
/// service is never asked to delete it. Edits validate the posted model before writing,
/// and invalid edits retain their attempted values and field errors.
/// </summary>
public class ShiftAdminControllerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid OtherTeamId = Guid.NewGuid();
    private const string Slug = "gate";

    private readonly IShiftManagement _shiftMgmt = Substitute.For<IShiftManagement>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IShiftRowView _shiftView = Substitute.For<IShiftRowView>();
    private readonly IVolunteerTrackingManagement _tracking = Substitute.For<IVolunteerTrackingManagement>();
    private readonly IStringLocalizer<ShiftsResource> _localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
    private readonly ILogger<ShiftAdminController> _logger = Substitute.For<ILogger<ShiftAdminController>>();
    private readonly IRotaCoordinatorMessageService _rotaMessenger = Substitute.For<IRotaCoordinatorMessageService>();

    private static readonly BurnSettingsInfo Event = new(
        Id: Guid.NewGuid(),
        EventName: "Test Event 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: -14,
        SetupWeekStartOffset: -10,
        PreEventWeekStartOffset: -7,
        FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    public ShiftAdminControllerTests()
    {
        _shiftMgmt.DeleteRotaAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(new ShiftDeletionResult());
        _shiftMgmt.DeleteShiftAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(new ShiftDeletionResult());
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(UserId));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo> { [TeamId] = MakeTeam(TeamId, Slug) });
        // The caller coordinates the URL's department and nothing else.
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId).Returns(true);
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, OtherTeamId).Returns(false);
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        _teamService.GetTeamAsync(TeamId, Arg.Any<CancellationToken>()).Returns(MakeTeam(TeamId, Slug));
        _shiftMgmt.GetTagsAsync().Returns([]);
        _shiftMgmt.GetStaffingSnapshotAsync(Event.Id, TeamId).Returns(ShiftStaffingSnapshot.Empty);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([]);
    }

    [HumansFact]
    public async Task Index_PreservesRequestCancellationThroughAuthorizationAndPageReads()
    {
        using var cancellation = new CancellationTokenSource();
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = cancellation.Token;
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId, cancellation.Token).Returns(true);
        _shiftMgmt.GetTagsAsync(ct: Arg.Any<CancellationToken>()).Returns([]);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id, Arg.Any<CancellationToken>()).Returns([]);
        _shiftMgmt.GetStaffingSnapshotAsync(Event.Id, TeamId, ct: Arg.Any<CancellationToken>())
            .Returns(ShiftStaffingSnapshot.Empty);

        var result = await controller.Index(Slug, ct: cancellation.Token);

        result.Should().BeOfType<ViewResult>();
        await _userService.Received(1).GetUserInfoAsync(UserId, cancellation.Token);
        await _teamService.Received().GetTeamsAsync(cancellation.Token);
        await _shiftMgmt.Received().IsDeptCoordinatorAsync(UserId, TeamId, cancellation.Token);
        await _teamService.Received(1).GetTeamAsync(TeamId, cancellation.Token);
        await _shiftMgmt.Received(1).GetRotasByDepartmentAsync(TeamId, Event.Id, cancellation.Token);
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CoordinatorGetReads_PreserveRequestCancellation(int page)
    {
        using var cancellation = new CancellationTokenSource();
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = cancellation.Token;
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId, Arg.Any<CancellationToken>()).Returns(true);
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        _shiftMgmt.GetRotaByIdAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(rota);
        _shiftMgmt.GetShiftByIdAsync(shift.Id, Arg.Any<CancellationToken>()).Returns(shift);
        _shiftView.GetRotaAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(new ShiftRotaView(
            rota.Id, rota, [], [], [new ShiftSignup { UserId = UserId, Status = SignupStatus.Confirmed }]));
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo> { [UserId] = MakeUserInfo(UserId) });
        _rotaMessenger.GetTeamRotasRecipientPreviewAsync(TeamId, Arg.Any<TeamRotasAudienceFilter>(), Arg.Any<CancellationToken>())
            .Returns(new TeamRotasRecipientPreview(1, ["Coordinator"]));
        _shiftMgmt.GetTagsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = page switch
        {
            0 => await controller.EmailRota(Slug, rota.Id, cancellation.Token),
            1 => await controller.EmailTeamRotas(Slug, cancellation.Token),
            2 => await controller.SearchVolunteers(Slug, shift.Id, "", cancellation.Token),
            _ => await controller.SearchTags(Slug, "tag", cancellation.Token),
        };

        Assert.NotNull(result);
        await _userService.Received(1).GetUserInfoAsync(UserId, cancellation.Token);
        await _teamService.Received(1).GetTeamsAsync(cancellation.Token);
        await _shiftMgmt.Received(1).IsDeptCoordinatorAsync(UserId, TeamId, cancellation.Token);
        switch (page)
        {
            case 0:
                await _shiftMgmt.Received(1).GetRotaByIdAsync(rota.Id, cancellation.Token);
                await _shiftView.Received(1).GetRotaAsync(rota.Id, cancellation.Token);
                await _userService.Received(1).GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), cancellation.Token);
                break;
            case 1:
                await _rotaMessenger.Received(1).GetTeamRotasRecipientPreviewAsync(
                    TeamId, TeamRotasAudienceFilter.Default, cancellation.Token);
                break;
            case 2:
                await _shiftMgmt.Received(1).GetShiftByIdAsync(shift.Id, cancellation.Token);
                break;
            default:
                await _shiftMgmt.Received(1).GetTagsAsync("tag", cancellation.Token);
                break;
        }
    }

    [HumansFact]
    public async Task VolunteerSearch_CallerCancellation_PropagatesInsteadOfReturningFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var rota = MakeRota(TeamId);
        rota.EventSettingsId = Event.Id;
        var shift = MakeShift(rota);
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId, Arg.Any<CancellationToken>()).Returns(true);
        _shiftMgmt.GetShiftByIdAsync(shift.Id, Arg.Any<CancellationToken>()).Returns(shift);
        _userService.SearchUsersAsync("ann", PersonSearchFields.Name, int.MaxValue, cancellation.Token)
            .Returns(async _ =>
            {
                await cancellation.CancelAsync();
                return await Task.FromCanceled<IReadOnlyList<HumanSearchResult>>(cancellation.Token);
            });
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = cancellation.Token;

        Func<Task> search = () => controller.SearchVolunteers(Slug, shift.Id, "ann", cancellation.Token);

        await search.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("startDayOffset", true, false)]
    [InlineData("endDayOffset", true, false)]
    [InlineData("startDayOffset", false, false)]
    [InlineData("endDayOffset", false, false)]
    [InlineData("startDayOffset", true, true)]
    [InlineData("endDayOffset", true, true)]
    [InlineData("startDayOffset", false, true)]
    [InlineData("endDayOffset", false, true)]
    [InlineData(null, true, false)]
    public async Task VoluntellRange_InvalidOrMissingOffset_DoesNotAssign(string? field, bool ownDepartment, bool missingOffset)
    {
        var rota = MakeRota(ownDepartment ? TeamId : OtherTeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        var beneficiary = Guid.NewGuid();
        _signupService.VoluntellRangeAsync(beneficiary, rota.Id, 0, 0, UserId)
            .Returns(SignupResult.Ok(Guid.NewGuid()));
        var controller = BuildSut();
        if (field is not null && !missingOffset) controller.ModelState.AddModelError(field, "Not a number");
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        if (missingOffset)
        {
            controller.HttpContext.RequestServices = services;
            var parameter = typeof(ShiftAdminController).GetMethod(nameof(ShiftAdminController.VoluntellRange))!
                .GetParameters().Single(p => string.Equals(p.Name, field, StringComparison.Ordinal));
            var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
            var descriptor = new ControllerParameterDescriptor
            {
                Name = parameter.Name!,
                ParameterType = parameter.ParameterType,
                ParameterInfo = parameter
            };
            var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = metadata,
                CacheToken = parameter
            });
            await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
                new FormValueProvider(BindingSource.Form, new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)), CultureInfo.InvariantCulture),
                descriptor, metadata, value: null);
        }

        var result = await controller.VoluntellRange(Slug, rota.Id, 0, 0, beneficiary);

        if (!ownDepartment)
            result.Should().BeOfType<NotFoundResult>();
        else if (field is not null)
            result.Should().BeOfType<BadRequestObjectResult>();
        else
            result.Should().BeOfType<RedirectToActionResult>();
        if (!ownDepartment || field is not null)
            await _signupService.DidNotReceiveWithAnyArgs().VoluntellRangeAsync(default, default, default, default, default);
        else
            await _signupService.Received(1).VoluntellRangeAsync(beneficiary, rota.Id, 0, 0, UserId);
    }

    [HumansTheory]
    [InlineData("Staffing", false)]
    [InlineData("Staffing", true)]
    [InlineData("Generation", false)]
    [InlineData("Generation", true)]
    public async Task Generation_RejectsMalformedBindings_ButAcceptsValidDefaults(string action, bool malformed)
    {
        var staffing = string.Equals(action, "Staffing", StringComparison.Ordinal);
        var rotaId = Guid.NewGuid();
        _shiftMgmt.CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>())
            .Returns(ShiftGenerationResult.Success("Created", 1));
        _shiftMgmt.GenerateEventShiftsAsync(Arg.Any<GenerateEventShiftsInput>())
            .Returns(ShiftGenerationResult.Success("Created", 1));
        var controller = BuildSut();
        if (malformed)
            controller.ModelState.AddModelError(staffing ? "Days[0].MinVolunteers" : "StartDayOffset",
                "Not a number.");

        var result = staffing
            ? await controller.ConfigureStaffing(Slug, rotaId,
                new StaffingGridModel { Days = [new DayStaffingEntry { DayOffset = -1 }] })
            : await controller.GenerateShifts(Slug, rotaId,
                new GenerateEventShiftsModel { StartDayOffset = 0, EndDayOffset = 0, TimeSlots = [new TimeSlotEntry()] });

        if (malformed)
        {
            result.Should().BeOfType<BadRequestObjectResult>();
            await _shiftMgmt.DidNotReceive().CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>());
            await _shiftMgmt.DidNotReceive().GenerateEventShiftsAsync(Arg.Any<GenerateEventShiftsInput>());
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            if (staffing)
                await _shiftMgmt.Received(1).CreateBuildStrikeShiftsAsync(Arg.Is<ConfigureBuildStrikeStaffingInput>(
                    input => input.RotaId == rotaId && input.TeamId == TeamId
                        && input.Days.Single().DayOffset == -1 && input.Days.Single().MinVolunteers == 2));
            else
                await _shiftMgmt.Received(1).GenerateEventShiftsAsync(Arg.Is<GenerateEventShiftsInput>(
                    input => input.RotaId == rotaId && input.TeamId == TeamId && input.StartDayOffset == 0));
        }
    }

    [HumansTheory]
    [InlineData(true, "Days[1].DayOffset", false, true)]
    [InlineData(true, "Days[1].MinVolunteers", false, true)]
    [InlineData(true, "Days[1].MaxVolunteers", false, true)]
    [InlineData(false, "StartDayOffset", false, true)]
    [InlineData(false, "EndDayOffset", false, true)]
    [InlineData(false, "MinVolunteers", false, true)]
    [InlineData(false, "MaxVolunteers", false, true)]
    [InlineData(false, "TimeSlots[1].StartTime", false, true)]
    [InlineData(false, "TimeSlots[1].DurationHours", false, true)]
    [InlineData(true, "", false, true)]
    [InlineData(true, "", true, true)]
    [InlineData(false, "", false, true)]
    [InlineData(false, "", true, true)]
    [InlineData(true, "Days[1].DayOffset", false, false)]
    [InlineData(false, "StartDayOffset", false, false)]
    public async Task Bulk_shift_forms_require_complete_days_slots_and_staffing(bool staffing, string omitted, bool zero, bool allowed)
    {
        var rotaId = Guid.NewGuid();
        _shiftMgmt.IsDeptCoordinatorAsync(UserId, TeamId).Returns(allowed);
        _shiftMgmt.CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>())
            .Returns(ShiftGenerationResult.Success("Created", 2));
        _shiftMgmt.GenerateEventShiftsAsync(Arg.Any<GenerateEventShiftsInput>())
            .Returns(ShiftGenerationResult.Success("Created", 2));
        var controller = BuildSut();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var minimum = zero ? 0 : 3;
        var maximum = zero ? 1 : 7;
        var firstDay = zero ? 0 : -2;
        var lastDay = zero ? 1 : -1;
        var start = zero || string.Equals(omitted, "EndDayOffset", StringComparison.Ordinal) ? 0 : 2;
        var end = zero ? 0 : 3;
        var duration = zero ? 0.5d : 2.5d;
        var fields = staffing ? new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["Days[0].DayOffset"] = firstDay.ToString(CultureInfo.InvariantCulture),
            ["Days[0].MinVolunteers"] = minimum.ToString(CultureInfo.InvariantCulture),
            ["Days[0].MaxVolunteers"] = maximum.ToString(CultureInfo.InvariantCulture),
            ["Days[1].DayOffset"] = lastDay.ToString(CultureInfo.InvariantCulture),
            ["Days[1].MinVolunteers"] = minimum.ToString(CultureInfo.InvariantCulture),
            ["Days[1].MaxVolunteers"] = maximum.ToString(CultureInfo.InvariantCulture)
        } : new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["StartDayOffset"] = start.ToString(CultureInfo.InvariantCulture),
            ["EndDayOffset"] = end.ToString(CultureInfo.InvariantCulture),
            ["MinVolunteers"] = minimum.ToString(CultureInfo.InvariantCulture),
            ["MaxVolunteers"] = maximum.ToString(CultureInfo.InvariantCulture),
            ["TimeSlots[0].StartTime"] = "00:00",
            ["TimeSlots[0].DurationHours"] = duration.ToString(CultureInfo.InvariantCulture),
            ["TimeSlots[1].StartTime"] = zero ? "01:00" : "09:30",
            ["TimeSlots[1].DurationHours"] = duration.ToString(CultureInfo.InvariantCulture)
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var formType = staffing ? typeof(StaffingGridModel) : typeof(GenerateEventShiftsModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(formType);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = formType
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, binding.ValidationState, "", binding.Result.Model!);

        var result = staffing
            ? await controller.ConfigureStaffing(Slug, rotaId, (StaffingGridModel)binding.Result.Model!)
            : await controller.GenerateShifts(Slug, rotaId, (GenerateEventShiftsModel)binding.Result.Model!);

        if (!allowed || omitted.Length > 0)
        {
            await _shiftMgmt.DidNotReceive().CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>());
            await _shiftMgmt.DidNotReceive().GenerateEventShiftsAsync(Arg.Any<GenerateEventShiftsInput>());
            if (!allowed) result.Should().BeOfType<ForbidResult>();
            else
            {
                result.Should().BeOfType<BadRequestObjectResult>();
                controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            }
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            if (staffing)
                await _shiftMgmt.Received(1).CreateBuildStrikeShiftsAsync(Arg.Is<ConfigureBuildStrikeStaffingInput>(input =>
                    input.RotaId == rotaId && input.TeamId == TeamId && input.Days.Count == 2
                    && input.Days[0].DayOffset == firstDay && input.Days[1].DayOffset == lastDay
                    && input.Days.All(day => day.MinVolunteers == minimum && day.MaxVolunteers == maximum)));
            else
            {
                var expectedTime = zero ? new LocalTime(1, 0) : new LocalTime(9, 30);
                await _shiftMgmt.Received(1).GenerateEventShiftsAsync(Arg.Is<GenerateEventShiftsInput>(input =>
                    input.RotaId == rotaId && input.TeamId == TeamId && input.StartDayOffset == start && input.EndDayOffset == end
                    && input.MinVolunteers == minimum && input.MaxVolunteers == maximum && input.TimeSlots.Count == 2
                    && input.TimeSlots[1].StartTime == expectedTime && input.TimeSlots.All(slot => slot.DurationHours == duration)));
            }
        }
    }

    [HumansTheory]
    [InlineData("ConfigureStaffing")]
    [InlineData("DeleteRota")]
    [InlineData("BailRange")]
    public async Task GuardrailRejection_LogsWarningWithReasonWithoutException(string action)
    {
        const string reason = "Shift operation rejected by a guardrail";
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        _shiftMgmt.CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>())
            .Returns(Task.FromException<ShiftGenerationResult>(new InvalidOperationException(reason)));
        _shiftMgmt.DeleteRotaAsync(rota.Id, UserId).Returns(new ShiftDeletionResult("Shifts_Delete_ConfirmedSignups", 2));
        _localizer["Shifts_Delete_ConfirmedSignups", Arg.Any<object[]>()]
            .Returns(new LocalizedString("Shifts_Delete_ConfirmedSignups", reason));
        var signupBlockId = Guid.NewGuid();
        _signupService.BailRangeAsync(signupBlockId, UserId)
            .Returns(new BailRangeResult("Shifts_BailRange_NotAuthorized"));
        _localizer["Shifts_BailRange_NotAuthorized"].Returns(new LocalizedString("Shifts_BailRange_NotAuthorized", reason));
        var ctrl = BuildSut();

        var result = action switch
        {
            "ConfigureStaffing" => await ctrl.ConfigureStaffing(Slug, rota.Id, new StaffingGridModel()),
            "DeleteRota" => await ctrl.DeleteRota(Slug, rota.Id),
            "BailRange" => await ctrl.BailRange(Slug, signupBlockId, null),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        result.Should().BeOfType<RedirectToActionResult>();
        ctrl.TempData["ErrorMessage"].Should().Be(reason);
        var arguments = _logger.ReceivedCalls().Should().ContainSingle(call =>
            string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).Subject.GetArguments();
        arguments[0].Should().Be(LogLevel.Warning);
        arguments[3].Should().BeNull();
        arguments[2]!.ToString().Should().Contain(action switch
        {
            "BailRange" => "Shifts_BailRange_NotAuthorized",
            "DeleteRota" => "Shifts_Delete_ConfirmedSignups",
            _ => reason
        });
    }

    [HumansFact]
    public async Task DeleteRota_OtherTeamsRota_IsNotFound_AndNotDeleted()
    {
        var rota = MakeRota(OtherTeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);

        var result = await BuildSut().DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().DeleteRotaAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task DeleteRota_OwnTeamsRota_Deletes()
    {
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);

        var result = await BuildSut().DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _shiftMgmt.Received(1).DeleteRotaAsync(rota.Id, UserId);
    }

    [HumansFact]
    public async Task DeleteShift_OtherTeamsShift_IsNotFound_AndNotDeleted()
    {
        var shift = MakeShift(MakeRota(OtherTeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);

        var result = await BuildSut().DeleteShift(Slug, shift.Id);

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().DeleteShiftAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task DeleteShift_OwnTeamsShift_Deletes()
    {
        var shift = MakeShift(MakeRota(TeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);

        var result = await BuildSut().DeleteShift(Slug, shift.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _shiftMgmt.Received(1).DeleteShiftAsync(shift.Id, UserId);
    }

    [HumansTheory]
    [InlineData(false, "Priority", false, true)]
    [InlineData(false, "Policy", false, true)]
    [InlineData(false, "Period", false, true)]
    [InlineData(true, "Priority", false, true)]
    [InlineData(true, "Policy", false, true)]
    [InlineData(true, "Period", false, true)]
    [InlineData(false, "", false, true)]
    [InlineData(false, "", true, true)]
    [InlineData(true, "", false, true)]
    [InlineData(true, "", true, true)]
    [InlineData(true, "Policy", false, false)]
    public async Task Rota_forms_require_explicit_priority_policy_and_period(bool edit, string omitted, bool zeroChoices, bool ownTeam)
    {
        var rota = MakeRota(ownTeam ? TeamId : OtherTeamId);
        rota.EventSettingsId = Event.Id;
        rota.Priority = ShiftPriority.Essential;
        rota.Policy = SignupPolicy.RequireApproval;
        rota.Period = RotaPeriod.Build;
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([rota]);
        var controller = BuildSut();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var priority = zeroChoices ? ShiftPriority.Normal : ShiftPriority.Essential;
        var policy = zeroChoices ? SignupPolicy.Public : SignupPolicy.RequireApproval;
        var period = zeroChoices || omitted.Length > 0 ? RotaPeriod.Build : RotaPeriod.Strike;
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "Changed rota",
            ["Priority"] = priority.ToString(),
            ["Policy"] = policy.ToString(),
            ["Period"] = period.ToString()
        };
        if (omitted.Length > 0) fields.Remove(omitted);
        var formType = edit ? typeof(EditRotaModel) : typeof(CreateRotaModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(formType);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = formType
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (CreateRotaModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, binding.ValidationState, "", model);

        var result = edit ? await controller.EditRota(Slug, rota.Id, (EditRotaModel)model) : await controller.CreateRota(Slug, model);

        if (!ownTeam || omitted.Length > 0)
        {
            if (!ownTeam) result.Should().BeOfType<NotFoundResult>();
            else
            {
                if (edit) result.Should().BeOfType<ViewResult>();
                else result.Should().BeOfType<RedirectToActionResult>();
                controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            }
            rota.Name.Should().Be("Rota");
            rota.Priority.Should().Be(ShiftPriority.Essential);
            rota.Policy.Should().Be(SignupPolicy.RequireApproval);
            rota.Period.Should().Be(RotaPeriod.Build);
            await _shiftMgmt.DidNotReceive().CreateRotaAsync(Arg.Any<Rota>(), Arg.Any<IReadOnlyList<Guid>?>());
            await _shiftMgmt.DidNotReceive().UpdateRotaAsync(Arg.Any<Rota>(), Arg.Any<IReadOnlyList<Guid>?>());
        }
        else if (edit)
        {
            result.Should().BeOfType<RedirectToActionResult>();
            rota.Priority.Should().Be(priority);
            rota.Policy.Should().Be(policy);
            rota.Period.Should().Be(period);
            await _shiftMgmt.Received(1).UpdateRotaAsync(rota, Arg.Any<IReadOnlyList<Guid>?>());
        }
        else
        {
            result.Should().BeOfType<RedirectResult>();
            await _shiftMgmt.Received(1).CreateRotaAsync(Arg.Is<Rota>(created =>
                created.Name == "Changed rota" && created.TeamId == TeamId && created.EventSettingsId == Event.Id
                && created.Priority == priority && created.Policy == policy && created.Period == period), Arg.Any<IReadOnlyList<Guid>?>());
        }
    }

    [HumansFact]
    public async Task EditRota_InvalidModel_RendersAttemptedEditWithoutChangingRota()
    {
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([rota]);
        var ctrl = BuildSut();
        ctrl.ModelState.SetModelValue(nameof(EditRotaModel.Name), "", "");
        ctrl.ModelState.AddModelError(nameof(EditRotaModel.Name), "required");
        var posted = new EditRotaModel { Name = "", Description = "attempted description", TagIds = Guid.NewGuid().ToString(), Priority = ShiftPriority.Essential };

        var result = await ctrl.EditRota(Slug, rota.Id, posted);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Index");
        view.ViewData["InvalidRotaEdit"].Should().BeSameAs(posted);
        posted.RotaId.Should().Be(rota.Id);
        view.ViewData.ModelState[nameof(EditRotaModel.Name)]!.Errors.Should().ContainSingle();
        var page = view.Model.Should().BeOfType<ShiftAdminViewModel>().Subject;
        page.Rotas.Should().ContainSingle().Which.Should().BeSameAs(rota);
        page.CanManageShifts.Should().BeTrue();
        rota.Name.Should().Be("Rota");
        rota.Description.Should().BeNull();
        await _shiftMgmt.DidNotReceive().UpdateRotaAsync(Arg.Any<Rota>(), Arg.Any<IReadOnlyList<Guid>?>());
    }

    [HumansTheory]
    [InlineData(false, "DayOffset", false, true, false)]
    [InlineData(false, "StartTime", false, true, false)]
    [InlineData(false, "DurationHours", false, true, false)]
    [InlineData(false, "MinVolunteers", false, true, false)]
    [InlineData(false, "MaxVolunteers", false, true, false)]
    [InlineData(true, "DayOffset", false, true, false)]
    [InlineData(true, "StartTime", false, true, false)]
    [InlineData(true, "DurationHours", false, true, false)]
    [InlineData(true, "MinVolunteers", false, true, false)]
    [InlineData(true, "MaxVolunteers", false, true, false)]
    [InlineData(true, "DurationHours", false, true, true)]
    [InlineData(false, "", false, true, false)]
    [InlineData(false, "", true, true, false)]
    [InlineData(true, "", false, true, false)]
    [InlineData(true, "", true, true, false)]
    [InlineData(true, "", false, true, true)]
    [InlineData(true, "StartTime", false, false, false)]
    public async Task Shift_forms_require_explicit_date_time_duration_and_staffing(bool edit, string omitted, bool zeroChoices, bool ownTeam, bool allDay)
    {
        var rota = MakeRota(ownTeam ? TeamId : OtherTeamId);
        rota.EventSettingsId = Event.Id;
        rota.Period = allDay ? RotaPeriod.All : RotaPeriod.Event;
        var shift = MakeShift(rota);
        shift.IsAllDay = allDay;
        rota.Shifts.Add(shift);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([rota]);
        _shiftMgmt.CreateShiftAsync(Arg.Any<CreateShiftInput>()).Returns(ShiftMutationResult.Success("Created", shift.Id));
        _shiftMgmt.UpdateShiftAsync(Arg.Any<UpdateShiftInput>()).Returns(ShiftMutationResult.Success("Updated", shift.Id));
        var controller = BuildSut();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        controller.HttpContext.RequestServices = services;
        var day = zeroChoices ? 0 : 2;
        var start = zeroChoices || allDay ? "00:00" : "09:30";
        var duration = allDay ? 24d : zeroChoices ? 0.5d : 2.5d;
        var minimum = zeroChoices ? 0 : 3;
        var maximum = zeroChoices ? 1 : 7;
        var fields = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["RotaId"] = rota.Id.ToString(),
            ["DayOffset"] = day.ToString(CultureInfo.InvariantCulture),
            ["StartTime"] = start,
            ["DurationHours"] = duration.ToString(CultureInfo.InvariantCulture),
            ["MinVolunteers"] = minimum.ToString(CultureInfo.InvariantCulture),
            ["MaxVolunteers"] = maximum.ToString(CultureInfo.InvariantCulture)
        };
        if (!zeroChoices) fields["AdminOnly"] = "true";
        if (omitted.Length > 0) fields.Remove(omitted);
        var formType = edit ? typeof(EditShiftModel) : typeof(CreateShiftModel);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(formType);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = formType
        });
        var binding = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(binding);
        var model = (CreateShiftModel)binding.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, binding.ValidationState, "", model);

        var result = edit ? await controller.EditShift(Slug, shift.Id, (EditShiftModel)model) : await controller.CreateShift(Slug, model);

        if (!ownTeam || omitted.Length > 0)
        {
            await _shiftMgmt.DidNotReceive().CreateShiftAsync(Arg.Any<CreateShiftInput>());
            await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
            if (!ownTeam) result.Should().BeOfType<NotFoundResult>();
            else
            {
                if (edit) result.Should().BeOfType<ViewResult>();
                else result.Should().BeOfType<RedirectToActionResult>();
                controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            }
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            controller.ModelState.IsValid.Should().BeTrue();
            var expectedTime = zeroChoices || allDay ? LocalTime.Midnight : new LocalTime(9, 30);
            if (edit)
                await _shiftMgmt.Received(1).UpdateShiftAsync(Arg.Is<UpdateShiftInput>(input => input.ShiftId == shift.Id
                    && input.TeamId == TeamId && input.DayOffset == day && input.StartTime == expectedTime
                    && input.DurationHours == duration && input.MinVolunteers == minimum && input.MaxVolunteers == maximum
                    && input.AdminOnly == !zeroChoices));
            else
                await _shiftMgmt.Received(1).CreateShiftAsync(Arg.Is<CreateShiftInput>(input => input.RotaId == rota.Id
                    && input.TeamId == TeamId && input.DayOffset == day && input.StartTime == expectedTime
                    && input.DurationHours == duration && input.MinVolunteers == minimum && input.MaxVolunteers == maximum
                    && input.AdminOnly == !zeroChoices));
        }
    }

    [HumansFact]
    public async Task EditShift_InvalidNumber_RendersRawAttemptAndPageDataWithoutSaving()
    {
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        rota.Shifts.Add(shift);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([rota]);
        var ctrl = BuildSut();
        ctrl.ModelState.SetModelValue(nameof(EditShiftModel.DurationHours), "not a number", "not a number");
        ctrl.ModelState.AddModelError(nameof(EditShiftModel.DurationHours), "Invalid number");
        var posted = new EditShiftModel { StartTime = "09:30", Description = "attempted description", AdminOnly = true };

        var result = await ctrl.EditShift(Slug, shift.Id, posted);

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Index");
        view.ViewData["InvalidShiftEdit"].Should().BeSameAs(posted);
        posted.ShiftId.Should().Be(shift.Id);
        view.ViewData.ModelState[nameof(EditShiftModel.DurationHours)]!.AttemptedValue.Should().Be("not a number");
        view.ViewData.ModelState[nameof(EditShiftModel.DurationHours)]!.Errors.Should().ContainSingle();
        view.Model.Should().BeOfType<ShiftAdminViewModel>().Subject.Rotas.Should().ContainSingle();
        shift.StartTime.Should().Be(new LocalTime(8, 0));
        shift.Description.Should().BeNull();
        shift.AdminOnly.Should().BeFalse();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    [HumansFact]
    public async Task EditShift_InvalidStartTime_RendersFieldErrorWithoutSaving()
    {
        var shift = MakeShift(MakeRota(TeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        var ctrl = BuildSut();
        var result = await ctrl.EditShift(Slug, shift.Id, new EditShiftModel { StartTime = "bad time" });

        var view = result.Should().BeOfType<ViewResult>().Subject;
        view.ViewName.Should().Be("Index");
        view.ViewData.ModelState[nameof(EditShiftModel.StartTime)]!.Errors.Should().ContainSingle();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    [HumansFact]
    public async Task EditShift_OtherTeamsShift_IsNotFoundWithoutSaving()
    {
        var shift = MakeShift(MakeRota(OtherTeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        var result = await BuildSut().EditShift(Slug, shift.Id, new EditShiftModel { StartTime = "08:00" });

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    [HumansFact]
    public async Task EmailTeamRotas_RefreshIntent_RepreviewsSelection_WithoutSending()
    {
        _rotaMessenger.GetTeamRotasRecipientPreviewAsync(
                TeamId, Arg.Any<TeamRotasAudienceFilter>(), Arg.Any<CancellationToken>())
            .Returns(new TeamRotasRecipientPreview(2, ["Bob", "Alice"]));
        var ctrl = BuildSut();
        // Half-written message: refreshing the audience is not a send, so it must
        // not trip the Message validator.
        ctrl.ModelState.AddModelError(nameof(EmailTeamRotasViewModel.Message), "required");
        var posted = new EmailTeamRotasViewModel { Message = "", UpcomingOnly = false, IncludeStrike = false };

        var result = await ctrl.EmailTeamRotas(Slug, posted, EmailTeamRotasViewModel.RefreshIntent);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(posted);
        posted.RecipientCount.Should().Be(2);
        posted.RecipientNames.Should().Equal("Alice", "Bob");
        await _rotaMessenger.Received(1).GetTeamRotasRecipientPreviewAsync(
            TeamId,
            Arg.Is<TeamRotasAudienceFilter>(f => !f.UpcomingOnly && !f.IncludeStrike && f.IncludeBuild && f.IncludeEvent),
            Arg.Any<CancellationToken>());
        await _rotaMessenger.DidNotReceiveWithAnyArgs().SendTeamRotasMessageAsync(
            default, default, null!, default, null!);
    }

    [HumansFact]
    public async Task EmailTeamRotas_Send_RepreviewsInsteadOfSending_WhenAudienceWasNeverPreviewed()
    {
        _rotaMessenger.GetTeamRotasRecipientPreviewAsync(
                TeamId, Arg.Any<TeamRotasAudienceFilter>(), Arg.Any<CancellationToken>())
            .Returns(new TeamRotasRecipientPreview(3, ["Alice", "Bob", "Cara"]));
        // No script: the coordinator widened the audience and pressed Send, so the
        // posted selection no longer matches the list the form is showing.
        var posted = new EmailTeamRotasViewModel
        {
            Message = "thank you",
            UpcomingOnly = false,
            PreviewedAudience = TeamRotasAudienceFilter.Default.Key,
        };
        var ctrl = BuildSut();
        // Model binding stored the stale key it posted. The hidden field's tag helper
        // prefers that over the model, so it has to be gone or the re-rendered form
        // posts the stale key again and the turn-back below never ends.
        ctrl.ModelState.SetModelValue(
            nameof(EmailTeamRotasViewModel.PreviewedAudience),
            TeamRotasAudienceFilter.Default.Key,
            TeamRotasAudienceFilter.Default.Key);

        var result = await ctrl.EmailTeamRotas(Slug, posted);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(posted);
        posted.AudienceChanged.Should().BeTrue();
        posted.RecipientCount.Should().Be(3);
        posted.PreviewedAudience.Should().Be(posted.Filter.Key);
        ctrl.ModelState.ContainsKey(nameof(EmailTeamRotasViewModel.PreviewedAudience))
            .Should().BeFalse();
        await _rotaMessenger.DidNotReceiveWithAnyArgs().SendTeamRotasMessageAsync(
            default, default, null!, default, null!);
    }

    [HumansFact]
    public async Task EmailTeamRotas_Send_PassesTheSelectedAudienceAndShiftChoice()
    {
        _rotaMessenger.GetTeamRotasRecipientPreviewAsync(
                TeamId, Arg.Any<TeamRotasAudienceFilter>(), Arg.Any<CancellationToken>())
            .Returns(new TeamRotasRecipientPreview(1, ["Alice"]));
        _rotaMessenger.SendTeamRotasMessageAsync(
                TeamId, UserId, "thank you", false, Arg.Any<TeamRotasAudienceFilter>(), Arg.Any<CancellationToken>())
            .Returns(TeamRotasMessageDispatchResult.Success(1, 1, "Gate"));
        var posted = new EmailTeamRotasViewModel
        {
            Message = "thank you",
            IncludeShifts = false,
            UpcomingOnly = false,
            IncludeEvent = false,
            // The audience this form's recipient list was previewed against.
            PreviewedAudience = new TeamRotasAudienceFilter(false, true, false, true).Key,
        };

        await BuildSut().EmailTeamRotas(Slug, posted);

        await _rotaMessenger.Received(1).SendTeamRotasMessageAsync(
            TeamId, UserId, "thank you", false,
            Arg.Is<TeamRotasAudienceFilter>(f => !f.UpcomingOnly && !f.IncludeEvent && f.IncludeBuild && f.IncludeStrike),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BailRange_DependencyFaultPropagatesWithoutFeedback()
    {
        var blockId = Guid.NewGuid();
        var fault = new InvalidOperationException("database diagnostic");
        _signupService.BailRangeAsync(blockId, UserId)
            .Returns(Task.FromException<BailRangeResult>(fault));
        var controller = BuildSut();

        var action = () => controller.BailRange(Slug, blockId, null);

        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        controller.TempData.Should().BeEmpty();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_DependencyFaultPropagatesWithoutRefusalFeedback(bool deleteShift)
    {
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        var failure = new InvalidOperationException("Private cascade delete diagnostic");
        _shiftMgmt.DeleteRotaAsync(rota.Id, UserId).Returns(Task.FromException<ShiftDeletionResult>(failure));
        _shiftMgmt.DeleteShiftAsync(shift.Id, UserId).Returns(Task.FromException<ShiftDeletionResult>(failure));
        var controller = BuildSut();
        Func<Task<IActionResult>> act = deleteShift
            ? () => controller.DeleteShift(Slug, shift.Id)
            : () => controller.DeleteRota(Slug, rota.Id);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(act);

        thrown.Should().BeSameAs(failure);
        controller.TempData.Should().BeEmpty();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("en", false)]
    [InlineData("es", false)]
    [InlineData("de", false)]
    [InlineData("it", false)]
    [InlineData("fr", false)]
    [InlineData("ca", false)]
    [InlineData("en", true)]
    [InlineData("es", true)]
    [InlineData("de", true)]
    [InlineData("it", true)]
    [InlineData("fr", true)]
    [InlineData("ca", true)]
    public async Task Delete_ConfirmedSignupRefusalIsLocalizedForCoordinators(string culture, bool deleteShift)
    {
        using var scope = new CultureScope(culture);
        var localizer = new StringLocalizer<ShiftsResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        _shiftMgmt.GetRotaByIdAsync(rota.Id).Returns(rota);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(shift);
        _shiftMgmt.DeleteRotaAsync(rota.Id, UserId).Returns(new ShiftDeletionResult("Shifts_Delete_ConfirmedSignups", 3));
        _shiftMgmt.DeleteShiftAsync(shift.Id, UserId).Returns(new ShiftDeletionResult("Shifts_Delete_ConfirmedSignups", 3));
        var controller = BuildSut(localizer);

        var result = deleteShift ? await controller.DeleteShift(Slug, shift.Id) : await controller.DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData["ErrorMessage"].Should().Be(localizer["Shifts_Delete_ConfirmedSignups", 3].Value);
        controller.TempData["ErrorMessage"].Should().BeOfType<string>().Which.Should().Contain("3").And.NotContain("{0}");
    }

    private ShiftAdminController BuildSut(IStringLocalizer<ShiftsResource>? localizer = null)
    {
        var ctrl = new ShiftAdminController(
            _teamService,
            _shiftMgmt,
            _burnSettings,
            _signupService,
            _shiftView,
            _userService,
            Substitute.For<IAuthorizationService>(),
            Substitute.For<IClock>(),
            new ShiftAdminPageBuilder(_shiftMgmt, Substitute.For<IMembershipCalculator>(), _userService, Substitute.For<ICantinaService>(), _teamService),
            new ShiftVolunteerSearchBuilder(_burnSettings, _userService, Substitute.For<ICantinaService>(), _shiftView, _signupService, _tracking),
            _rotaMessenger,
            _logger,
            localizer ?? _localizer);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test")),
        };
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        http.RequestServices = services.BuildServiceProvider();
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static Rota MakeRota(Guid teamId) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        EventSettingsId = Guid.NewGuid(),
        Name = "Rota",
        Policy = SignupPolicy.Public,
        Period = RotaPeriod.Event,
    };

    private static Shift MakeShift(Rota rota) => new()
    {
        Id = Guid.NewGuid(),
        RotaId = rota.Id,
        Rota = rota,
        DayOffset = 0,
        StartTime = new LocalTime(8, 0),
        Duration = Duration.FromHours(4),
        MaxVolunteers = 5,
    };

    private static TeamInfo MakeTeam(Guid id, string slug) => new(
        id, "Gate", null, slug,
        IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
        RequiresApproval: false, IsPublicPage: false, IsHidden: false,
        IsPromotedToDirectory: false, CreatedAt: Instant.MinValue, Members: []);

    private static UserInfo MakeUserInfo(Guid userId) => UserInfoFactory.Create(
        user: new User { Id = userId, DisplayName = "Coord", PreferredLanguage = "en" },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: new Profile { UserId = userId, BurnerName = "Coord" },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);
}
