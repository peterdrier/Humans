using Humans.Testing;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Humans.Consent.Contracts;
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
using Humans.Shifts.Contracts;
using Humans.Shifts.Controllers;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Permissions.Contracts;
using Humans.Base.Authorization;
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
/// The route id is untrusted: an id that belongs to another team is a 404 and never
/// reaches the service.
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
    private readonly IShiftRotaRows _rotaRows = Substitute.For<IShiftRotaRows>();
    private readonly IVolunteerTrackingManagement _tracking = Substitute.For<IVolunteerTrackingManagement>();
    private readonly IStringLocalizer<ShiftsResource> _localizer = Substitute.For<IStringLocalizer<ShiftsResource>>().AddFormatting();
    private readonly ILogger<ShiftAdminController> _logger = Substitute.For<ILogger<ShiftAdminController>>();
    private readonly IRotaCoordinatorMessageService _rotaMessenger = Substitute.For<IRotaCoordinatorMessageService>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();

    private static Owner Team(Guid id) => new(TeamOwner.Kind, id);

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
        _shiftMgmt.UpdateRotaAsync(Arg.Any<UpdateRotaInput>()).Returns(true);
        _shiftMgmt.DeleteShiftAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(new ShiftDeletionResult());
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(UserId));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, TeamInfo> { [TeamId] = MakeTeam(TeamId, Slug) });
        // The caller manages and approves on the URL's department and nothing else.
        _permissions.HasAsync(UserId, Arg.Any<string>(), Team(TeamId), Arg.Any<CancellationToken>()).Returns(true);
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Event);
        _teamService.GetTeamAsync(TeamId, Arg.Any<CancellationToken>()).Returns(MakeTeam(TeamId, Slug));
        _shiftMgmt.GetTagsAsync().Returns([]);
        _shiftMgmt.GetStaffingSnapshotAsync(Event.Id, TeamId).Returns(ShiftStaffingSnapshot.Empty);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([]);
    }

    [HumansTheory]
    [InlineData("tag", "viewer")]
    [InlineData("tag", "teams")]
    [InlineData("tag", "coordinator")]
    [InlineData("approve", "viewer")]
    [InlineData("approve", "teams")]
    [InlineData("approve", "coordinator")]
    [InlineData("edit-rota", "rota")]
    [InlineData("edit-shift", "shift")]
    [InlineData("invalid-rota", "settings")]
    [InlineData("invalid-shift", "settings")]
    [InlineData("email-rota", "view")]
    [InlineData("email-rota", "profiles")]
    [InlineData("email-team", "preview")]
    public async Task MutationPreflightReads_PropagateCancellationBeforeAnyWrite(string action, string boundary)
    {
        using var request = new CancellationTokenSource();
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = request.Token;
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        void Check(string read, CancellationToken ct)
        {
            if (string.Equals(boundary, read, StringComparison.Ordinal)) ct.ThrowIfCancellationRequested();
        }
        _userService.GetUserInfoAsync(UserId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("viewer", call.Arg<CancellationToken>());
            return new ValueTask<UserInfo?>(MakeUserInfo(UserId));
        });
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("teams", call.Arg<CancellationToken>());
            return Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(new Dictionary<Guid, TeamInfo> { [TeamId] = MakeTeam(TeamId, Slug) });
        });
        _permissions.HasAsync(UserId, Arg.Any<string>(), Team(TeamId), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("coordinator", call.Arg<CancellationToken>());
            return Task.FromResult(true);
        });
        _shiftMgmt.GetRotaAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("rota", call.Arg<CancellationToken>());
            return Task.FromResult<RotaInfo?>(ToInfo(rota));
        });
        _shiftMgmt.GetShiftByIdAsync(shift.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("shift", call.Arg<CancellationToken>());
            return Task.FromResult<ShiftWithRota?>(ToInfo(shift));
        });
        _burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("settings", call.Arg<CancellationToken>());
            return Task.FromResult<BurnSettingsInfo?>(Event);
        });
        _rotaRows.GetRotaRowsAsync(Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.Contains(rota.Id)), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("view", call.Arg<CancellationToken>());
            return Task.FromResult(RecipientRows(rota));
        });
        _userService.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("profiles", call.Arg<CancellationToken>());
            return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo> { [UserId] = MakeUserInfo(UserId) });
        });
        _rotaMessenger.GetTeamRotasRecipientPreviewAsync(TeamId, Arg.Any<TeamRotasAudienceFilter>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Check("preview", call.Arg<CancellationToken>());
            return Task.FromResult(new TeamRotasRecipientPreview(1, ["Coordinator"]));
        });
        controller.ModelState.AddModelError("Message", "Invalid");
        await request.CancelAsync();
        Func<Task<IActionResult>> read = () => action switch
        {
            "tag" => controller.CreateTag(Slug, ""),
            "approve" => controller.ApproveSignup(Slug, Guid.NewGuid()),
            "edit-rota" or "invalid-rota" => controller.EditRota(Slug, rota.Id, new EditRotaModel()),
            "edit-shift" or "invalid-shift" => controller.EditShift(Slug, shift.Id, new EditShiftModel()),
            "email-rota" => controller.EmailRota(Slug, rota.Id, new EmailRotaViewModel()),
            _ => controller.EmailTeamRotas(Slug, new EmailTeamRotasViewModel())
        };
        await read.Should().ThrowAsync<OperationCanceledException>();
        _shiftMgmt.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name.StartsWith("Get", StringComparison.Ordinal));
        _signupService.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == "GetTeamProbeAsync");
        _rotaMessenger.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == "GetTeamRotasRecipientPreviewAsync");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignupOwnershipProbe_CancelsBeforeApproval(bool block)
    {
        using var request = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var scope = block ? ShiftSignupTeamProbeScope.SignupBlock : ShiftSignupTeamProbeScope.Signup;
        _permissions.HasAsync(UserId, Arg.Any<string>(), Team(TeamId), Arg.Any<CancellationToken>()).Returns(true);
        _signupService.GetTeamProbeAsync(id, scope, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<ShiftSignupTeamProbe?>(null);
        });
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = request.Token;
        await request.CancelAsync();
        Func<Task<IActionResult>> action = block
            ? () => controller.ApproveRange(Slug, id)
            : () => controller.ApproveSignup(Slug, id);

        await action.Should().ThrowAsync<OperationCanceledException>();
        _signupService.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == "GetTeamProbeAsync");
    }

    [HumansFact]
    public async Task Index_RequestCancellation_ReachesPageReads()
    {
        using var cancellation = new CancellationTokenSource();
        var controller = BuildSut();
        controller.HttpContext.RequestAborted = cancellation.Token;
        _permissions.HasAsync(UserId, Arg.Any<string>(), Team(TeamId), cancellation.Token).Returns(true);
        _shiftMgmt.GetTagsAsync(ct: Arg.Any<CancellationToken>()).Returns([]);
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id, Arg.Any<CancellationToken>()).Returns([]);
        _shiftMgmt.GetStaffingSnapshotAsync(Event.Id, TeamId, ct: Arg.Any<CancellationToken>())
            .Returns(ShiftStaffingSnapshot.Empty);

        var result = await controller.Index(Slug, ct: cancellation.Token);

        result.Should().BeOfType<ViewResult>();
        await _userService.Received(1).GetUserInfoAsync(UserId, cancellation.Token);
        await _teamService.Received().GetTeamsAsync(cancellation.Token);
        await _permissions.Received().HasAsync(UserId, Arg.Any<string>(), Team(TeamId), cancellation.Token);
        await _teamService.DidNotReceiveWithAnyArgs().GetTeamAsync(default);
        await _shiftMgmt.Received(1).GetRotasByDepartmentAsync(TeamId, Event.Id, cancellation.Token);
    }

    [HumansTheory]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, false, false, null, null)]
    [InlineData(false, true, false, false, true)]
    [InlineData(true, false, false, true, false)]
    public async Task Index_ControlsFollowThePermissionsOnTheTeam(
        bool manage, bool approve, bool canRead, bool? canManage, bool? canApprove)
    {
        _permissions.HasAsync(UserId, ShiftsPermissions.Manage, Team(TeamId), Arg.Any<CancellationToken>()).Returns(manage);
        _permissions.HasAsync(UserId, ShiftsPermissions.Approve, Team(TeamId), Arg.Any<CancellationToken>()).Returns(approve);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), ShiftsPolicies.Read)
            .Returns(canRead ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var controller = BuildSut(authorization: authorization);

        var result = await controller.Index(Slug);

        if (canManage is null)
            result.Should().BeOfType<ForbidResult>();
        else
        {
            var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<ShiftAdminViewModel>().Subject;
            model.CanManageShifts.Should().Be(canManage.Value);
            model.CanApproveSignups.Should().Be(canApprove!.Value);
        }
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
        _permissions.HasAsync(UserId, Arg.Any<string>(), Team(TeamId), Arg.Any<CancellationToken>()).Returns(true);
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        _shiftMgmt.GetRotaAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(_ => ToInfo(rota));
        _shiftMgmt.GetShiftByIdAsync(shift.Id, Arg.Any<CancellationToken>()).Returns(_ => ToInfo(shift));
        _rotaRows.GetRotaRowsAsync(Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(RecipientRows(rota));
        _userService.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
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
        await _permissions.Received(1).HasAsync(UserId, Arg.Any<string>(), Team(TeamId), cancellation.Token);
        switch (page)
        {
            case 0:
                await _shiftMgmt.Received(1).GetRotaAsync(rota.Id, cancellation.Token);
                await _rotaRows.Received(1).GetRotaRowsAsync(Arg.Any<IReadOnlyCollection<Guid>?>(), cancellation.Token);
                await _userService.Received(1).GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), cancellation.Token);
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
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));
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
    public async Task BulkShiftForms_IncompleteInput_IsRejected(bool staffing, string omitted, bool zero, bool allowed)
    {
        var rotaId = Guid.NewGuid();
        _permissions.HasAsync(UserId, Arg.Any<string>(), Team(TeamId), Arg.Any<CancellationToken>()).Returns(allowed);
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
    [InlineData("GenerateShifts")]
    [InlineData("CreateShift")]
    [InlineData("MoveRota")]
    public async Task StaffingMutations_DoNotPresentUnexpectedFailuresAsGuardrailRefusals(string action)
    {
        var rota = MakeRota(TeamId);
        var failure = new InvalidOperationException("Unexpected persistence details");
        _shiftMgmt.CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>())
            .Returns(Task.FromException<ShiftGenerationResult>(failure));
        _shiftMgmt.GenerateEventShiftsAsync(Arg.Any<GenerateEventShiftsInput>())
            .Returns(Task.FromException<ShiftGenerationResult>(failure));
        _shiftMgmt.CreateShiftAsync(Arg.Any<CreateShiftInput>())
            .Returns(Task.FromException<ShiftMutationResult>(failure));
        _shiftMgmt.MoveRotaToTeamAsync(Arg.Any<MoveRotaInput>())
            .Returns(Task.FromException<RotaMoveResult>(failure));
        var ctrl = BuildSut();
        var act = async () => action switch
        {
            "ConfigureStaffing" => await ctrl.ConfigureStaffing(Slug, rota.Id, new StaffingGridModel()),
            "GenerateShifts" => await ctrl.GenerateShifts(Slug, rota.Id, new GenerateEventShiftsModel()),
            "CreateShift" => await ctrl.CreateShift(Slug, new CreateShiftModel { RotaId = rota.Id, StartTime = "09:00" }),
            "MoveRota" => await ctrl.MoveRota(Slug, rota.Id, new MoveRotaModel { TargetTeamId = Guid.NewGuid() }),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        ctrl.TempData.Should().BeEmpty();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("ConfigureStaffing")]
    [InlineData("GenerateShifts")]
    [InlineData("CreateShift")]
    [InlineData("MoveRota")]
    [InlineData("DeleteRota")]
    [InlineData("BailRange")]
    public async Task GuardrailRejection_LogsWarningWithReasonWithoutException(string action)
    {
        const string reason = "Shift operation rejected by a guardrail";
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));
        _shiftMgmt.CreateBuildStrikeShiftsAsync(Arg.Any<ConfigureBuildStrikeStaffingInput>())
            .Returns(ShiftGenerationResult.Failure(reason));
        _shiftMgmt.GenerateEventShiftsAsync(Arg.Any<GenerateEventShiftsInput>()).Returns(ShiftGenerationResult.Failure(reason));
        _shiftMgmt.CreateShiftAsync(Arg.Any<CreateShiftInput>()).Returns(ShiftMutationResult.Failure(reason));
        _shiftMgmt.MoveRotaToTeamAsync(Arg.Any<MoveRotaInput>()).Returns(RotaMoveResult.Failure(reason));
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
            "GenerateShifts" => await ctrl.GenerateShifts(Slug, rota.Id, new GenerateEventShiftsModel()),
            "CreateShift" => await ctrl.CreateShift(Slug, new CreateShiftModel { RotaId = rota.Id, StartTime = "09:00" }),
            "MoveRota" => await ctrl.MoveRota(Slug, rota.Id, new MoveRotaModel { TargetTeamId = Guid.NewGuid() }),
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

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToggleVisibility_FlipsOnlyVisibilityOfOwnTeamsRota(bool ownTeam)
    {
        var rota = MakeRota(ownTeam ? TeamId : OtherTeamId);
        rota.Description = "Keep me";
        rota.IsVisibleToVolunteers = true;
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));

        var result = await BuildSut().ToggleVisibility(Slug, rota.Id);

        if (!ownTeam)
        {
            result.Should().BeOfType<NotFoundResult>();
            await _shiftMgmt.DidNotReceive().UpdateRotaAsync(Arg.Any<UpdateRotaInput>());
            return;
        }

        result.Should().BeOfType<RedirectToActionResult>();
        await _shiftMgmt.Received(1).UpdateRotaAsync(Arg.Is<UpdateRotaInput>(i =>
            i.RotaId == rota.Id && i.Name == rota.Name && i.Description == "Keep me"
            && !i.IsVisibleToVolunteers && i.TagIds == null));
    }

    [HumansFact]
    public async Task DeleteRota_OtherTeamsRota_IsNotFound()
    {
        var rota = MakeRota(OtherTeamId);
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));

        var result = await BuildSut().DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().DeleteRotaAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task DeleteRota_OwnTeamsRota_Deletes()
    {
        var rota = MakeRota(TeamId);
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));

        var result = await BuildSut().DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _shiftMgmt.Received(1).DeleteRotaAsync(rota.Id, UserId);
    }

    [HumansFact]
    public async Task DeleteShift_OtherTeamsShift_IsNotFound()
    {
        var shift = MakeShift(MakeRota(OtherTeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));

        var result = await BuildSut().DeleteShift(Slug, shift.Id);

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().DeleteShiftAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task DeleteShift_OwnTeamsShift_Deletes()
    {
        var shift = MakeShift(MakeRota(TeamId));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));

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
    public async Task RotaForms_MissingChoices_AreRejected(bool edit, string omitted, bool zeroChoices, bool ownTeam)
    {
        var rota = MakeRota(ownTeam ? TeamId : OtherTeamId);
        rota.EventSettingsId = Event.Id;
        rota.Priority = ShiftPriority.Essential;
        rota.Policy = SignupPolicy.RequireApproval;
        rota.Period = RotaPeriod.Build;
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([ToAdminRota(rota)]);
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
            await _shiftMgmt.DidNotReceive().CreateRotaAsync(Arg.Any<Rota>(), Arg.Any<IReadOnlyList<Guid>?>());
            await _shiftMgmt.DidNotReceive().UpdateRotaAsync(Arg.Any<UpdateRotaInput>());
        }
        else if (edit)
        {
            result.Should().BeOfType<RedirectToActionResult>();
            await _shiftMgmt.Received(1).UpdateRotaAsync(Arg.Is<UpdateRotaInput>(i =>
                i.RotaId == rota.Id && i.Name == "Changed rota"
                && i.Priority == priority && i.Policy == policy && i.Period == period));
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
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([ToAdminRota(rota)]);
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
        page.Rotas.Should().ContainSingle().Which.Rota.Id.Should().Be(rota.Id);
        page.CanManageShifts.Should().BeTrue();
        await _shiftMgmt.DidNotReceive().UpdateRotaAsync(Arg.Any<UpdateRotaInput>());
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
    public async Task ShiftForms_MissingValues_AreRejected(bool edit, string omitted, bool zeroChoices, bool ownTeam, bool allDay)
    {
        var rota = MakeRota(ownTeam ? TeamId : OtherTeamId);
        rota.EventSettingsId = Event.Id;
        rota.Period = allDay ? RotaPeriod.All : RotaPeriod.Event;
        var shift = MakeShift(rota);
        shift.IsAllDay = allDay;
        rota.Shifts.Add(shift);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([ToAdminRota(rota)]);
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
    public async Task EditShift_InvalidNumber_RendersAttemptWithoutSaving()
    {
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        rota.Shifts.Add(shift);
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));
        _shiftMgmt.GetRotasByDepartmentAsync(TeamId, Event.Id).Returns([ToAdminRota(rota)]);
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
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));
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
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));
        var result = await BuildSut().EditShift(Slug, shift.Id, new EditShiftModel { StartTime = "08:00" });

        result.Should().BeOfType<NotFoundResult>();
        await _shiftMgmt.DidNotReceive().UpdateShiftAsync(Arg.Any<UpdateShiftInput>());
    }

    [HumansFact]
    public async Task EmailRota_ArchivedRecipients_PreviewsOneLiveHuman()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var rota = MakeRota(TeamId);
        var archivedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        _shiftMgmt.GetRotaAsync(rota.Id, Arg.Any<CancellationToken>()).Returns(ToInfo(rota));
        _rotaRows.GetRotaRowsAsync(Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns([new ShiftAdminRota(ToInfo(rota), [new ShiftAdminShift(ToShiftInfo(MakeShift(rota)),
                new[] { archivedId, survivorId, deletedId }.Select(id => new ShiftAdminSignup(Guid.NewGuid(), id, null,
                    SignupStatus.Confirmed, Instant.MinValue)).ToList())])]);
        var survivor = MakeUserInfo(survivorId) with { BurnerName = "Human", IdentityEmailColumn = "human@example.org" };
        _userService.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>
            {
                [archivedId] = survivor,
                [survivorId] = survivor,
                [deletedId] = MakeUserInfo(deletedId) with { BurnerName = "Deleted", State = UserState.Deleted }
            });
        var controller = BuildSut();

        var get = await controller.EmailRota(Slug, rota.Id, ct);
        get.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<EmailRotaViewModel>()
            .Which.RecipientNames.Should().Equal("Human");
        controller.ModelState.AddModelError(nameof(EmailRotaViewModel.Message), "required");
        var post = await controller.EmailRota(Slug, rota.Id, new EmailRotaViewModel());
        post.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<EmailRotaViewModel>()
            .Which.RecipientNames.Should().Equal("Human");
    }

    [HumansFact]
    public async Task EmailTeamRotas_RefreshIntent_RepreviewsWithoutSending()
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
    public async Task EmailTeamRotas_AudienceNeverPreviewed_RepreviewsInstead()
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
    public async Task EmailTeamRotas_Send_PassesSelectedAudience()
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
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_ConfirmedSignupRefusalIsLocalizedForCoordinators(bool deleteShift)
    {
        using var scope = new CultureScope(CultureCatalog.DefaultCultureCode);
        var localizer = new StringLocalizer<ShiftsResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var rota = MakeRota(TeamId);
        var shift = MakeShift(rota);
        _shiftMgmt.GetRotaAsync(rota.Id).Returns(_ => ToInfo(rota));
        _shiftMgmt.GetShiftByIdAsync(shift.Id).Returns(_ => ToInfo(shift));
        _shiftMgmt.DeleteRotaAsync(rota.Id, UserId).Returns(new ShiftDeletionResult("Shifts_Delete_ConfirmedSignups", 3));
        _shiftMgmt.DeleteShiftAsync(shift.Id, UserId).Returns(new ShiftDeletionResult("Shifts_Delete_ConfirmedSignups", 3));
        var controller = BuildSut(localizer);

        var result = deleteShift ? await controller.DeleteShift(Slug, shift.Id) : await controller.DeleteRota(Slug, rota.Id);

        result.Should().BeOfType<RedirectToActionResult>();
        controller.TempData["ErrorMessage"].Should().Be(localizer["Shifts_Delete_ConfirmedSignups", 3].Value);
        controller.TempData["ErrorMessage"].Should().BeOfType<string>().Which.Should().Contain("3").And.NotContain("{0}");
    }

    private ShiftAdminController BuildSut(
        IStringLocalizer<ShiftsResource>? localizer = null, IAuthorizationService? authorization = null)
    {
        var ctrl = new ShiftAdminController(
            _teamService,
            _shiftMgmt,
            _burnSettings,
            _signupService,
            _rotaRows,
            _userService,
            authorization ?? Substitute.For<IAuthorizationService>(),
            Substitute.For<IClock>(),
            new ShiftAdminPageBuilder(_shiftMgmt, Substitute.For<IConsentService>(), _userService, _teamService),
            new ShiftVolunteerSearchBuilder(_burnSettings, _userService, _shiftView, _signupService, _tracking),
            _rotaMessenger,
            _permissions,
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

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public async Task DurationForms_PostedValues_PreserveHours(string language)
    {
        using var culture = new CultureScope(language);
        using var assertions = new AwesomeAssertions.Execution.AssertionScope();
        var rota = MakeRota(TeamId);
        var timed = MakeShift(rota);
        timed.Duration = Duration.FromHours(4.5);
        var allDay = MakeShift(rota);
        allDay.IsAllDay = true;
        allDay.Duration = Duration.FromHours(4.5);
        var model = new ShiftAdminViewModel
        {
            Department = MakeTeam(TeamId, Slug),
            EventSettings = Event,
            CanManageShifts = true,
            Rotas =
            [
                new(ToInfo(rota), [new(ToShiftInfo(timed), []), new(ToShiftInfo(allDay), [])]),
                new(ToInfo(MakeRota(TeamId)), []),
            ],
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ShiftAdminControllerTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ShiftsResource).Assembly)
            .AddApplicationPart(typeof(Humans.Base.ViewComponents.HumanViewComponent).Assembly);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var view = services.GetRequiredService<IRazorViewEngine>().GetView(null, "/Views/ShiftAdmin/Index.cshtml", isMainPage: false);
        view.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "ShiftAdmin";
        route.Values["action"] = "Index";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        await view.View!.RenderAsync(new ViewContext(action, view.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions()));
        var tested = 0;
        foreach (Match form in Regex.Matches(writer.ToString(), "<form\\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.NonBacktracking))
        {
            var inputs = Regex.Matches(form.Value, "<input\\b[^>]*>", RegexOptions.NonBacktracking)
                .Cast<Match>().Select(input => Regex.Matches(input.Value, "(?<name>[\\w-]+)=\"(?<value>[^\"]*)\"", RegexOptions.NonBacktracking)
                    .Cast<Match>().ToDictionary(a => a.Groups["name"].Value, a => a.Groups["value"].Value, StringComparer.Ordinal)).ToList();
            var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
            foreach (var input in inputs.Where(i => i.ContainsKey("name")))
            {
                var name = input["name"];
                fields[name] = StringValues.Concat(fields.GetValueOrDefault(name), input.GetValueOrDefault("value", ""));
            }
            foreach (var input in inputs.Where(i => i.GetValueOrDefault("name") is "DurationHours" or "TimeSlots[0].DurationHours"))
            {
                var name = input["name"];
                input["value"].Should().BeOneOf("4", "4.5");
                fields[name] = "4.5";
                var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(double));
                var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
                var binding = DefaultModelBindingContext.CreateBindingContext(action,
                    new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.CurrentCulture),
                    metadata, bindingInfo: null, modelName: name);
                await binder.BindModelAsync(binding);
                binding.Result.IsModelSet.Should().BeTrue();
                binding.Result.Model.Should().Be(4.5d, $"{name} must preserve fractional hours in {language}");
                tested++;
            }
        }
        tested.Should().Be(5);
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

    private static IReadOnlyList<ShiftAdminRota> RecipientRows(Rota rota) =>
    [
        new(ToInfo(rota), [new ShiftAdminShift(ToShiftInfo(MakeShift(rota)),
            [new ShiftAdminSignup(Guid.NewGuid(), UserId, null, SignupStatus.Confirmed, Instant.MinValue)])])
    ];

    private static RotaInfo ToInfo(Rota rota) => new(
        rota.Id, rota.EventSettingsId, rota.TeamId, rota.Name, rota.Description, rota.PracticalInfo,
        rota.Priority, rota.Policy, rota.Period, rota.IsVisibleToVolunteers, []);

    private static ShiftWithRota ToInfo(Shift shift) => new(ToShiftInfo(shift), ToInfo(shift.Rota));

    private static ShiftInfo ToShiftInfo(Shift shift) => new(
        shift.Id, shift.RotaId, shift.Description, shift.DayOffset, shift.StartTime,
        shift.Duration.TotalHours, shift.MinVolunteers, shift.MaxVolunteers, shift.AdminOnly,
        shift.IsAllDay, shift.IsEarlyEntry);

    private static ShiftAdminRota ToAdminRota(Rota rota) => new(
        ToInfo(rota),
        [.. rota.Shifts.Select(s => new ShiftAdminShift(ToShiftInfo(s), []))]);

    private static TeamInfo MakeTeam(Guid id, string slug) => new(
        id, "Gate", null, slug,
        IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
        RequiresApproval: false, IsPublicPage: false, IsHidden: false,
        IsPromotedToDirectory: false, CreatedAt: Instant.MinValue, Members: []);

    private static UserInfo MakeUserInfo(Guid userId) => UserInfoFactory.Create(
        user: new User { Id = userId, DisplayName = "Coord", PreferredLanguage = "en" },
        userEmails: [],
        externalLogins: [],
        profile: new Profile { UserId = userId },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: []);
}
