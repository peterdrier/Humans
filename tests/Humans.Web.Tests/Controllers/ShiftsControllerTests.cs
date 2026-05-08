using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web.Constants;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies <see cref="ShiftsController"/> volunteer self-service action
/// <c>SaveBlockedDays</c>: anonymous → Challenge, signed-in user → 302 to
/// Mine with success TempData, the airtight UserId-from-ClaimsPrincipal
/// authorization story (no <c>UserId</c> form field exists, so injection
/// is structurally impossible to honor), unsorted/duplicate offsets pass
/// through to the service unchanged (sort/dedup is a service concern),
/// and the bulk-save audit fan-out (one row per Added/Removed plus a
/// single OwnBlockedDaysSaved row).
/// </summary>
public class ShiftsControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IGeneralAvailabilityService _availabilityService = Substitute.For<IGeneralAvailabilityService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly IVolunteerTrackingService _trackingService = Substitute.For<IVolunteerTrackingService>();
    private readonly IStringLocalizer<Humans.Web.SharedResource> _localizer =
        Substitute.For<IStringLocalizer<Humans.Web.SharedResource>>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ShiftsControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private ShiftsController BuildSut(User? currentUser)
    {
        if (currentUser is not null)
        {
            _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
        }
        else
        {
            _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns((User?)null);
        }

        var ctrl = new ShiftsController(
            _shiftMgmt,
            _signupService,
            _availabilityService,
            _teamService,
            _auditLog,
            _trackingService,
            _localizer,
            _userManager,
            _clock,
            NullLogger<ShiftsController>.Instance);

        var http = new DefaultHttpContext();
        if (currentUser is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString()) },
                "test"));
        }

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var urlHelperFactory = Substitute.For<IUrlHelperFactory>();
        urlHelperFactory.GetUrlHelper(Arg.Any<ActionContext>())
            .Returns(Substitute.For<IUrlHelper>());
        services.AddSingleton(urlHelperFactory);
        http.RequestServices = services.BuildServiceProvider();

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ActionName = "Test",
            },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    // ---------------------------------------------------------------------
    // SaveBlockedDays — authentication
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SaveBlockedDays_NoCurrentUser_ReturnsChallenge()
    {
        // Cookie maps to a deleted/inaccessible identity; [Authorize] passed
        // claims but the UserManager can't materialize a User row.
        var ctrl = BuildSut(currentUser: null);

        var result = await ctrl.SaveBlockedDays(new List<int> { -3 }, CancellationToken.None);

        Assert.IsType<ChallengeResult>(result);
        await _trackingService.DidNotReceive().SaveOwnBlockedDaysAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------
    // SaveBlockedDays — userId-from-ClaimsPrincipal authorization story.
    // The form has no UserId field, so an attacker injecting one is silently
    // dropped by model binding. We verify the service is called with
    // current.Id, not anything else.
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SaveBlockedDays_UsesCurrentUserId_NeverFromForm_RegressionForAttackVector()
    {
        // User A is signed in. Even if a malicious client posts UserId=B as
        // an extra form field, the SaveBlockedDays signature only binds
        // dayOffsets; UserId never reaches the service. The structural
        // proof: the action signature has no UserId parameter.
        var userA = new User { Id = Guid.NewGuid() };
        var userB = Guid.NewGuid(); // attacker's target — never used
        _trackingService.SaveOwnBlockedDaysAsync(userA.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: true, Added: Array.Empty<int>(), Removed: Array.Empty<int>(),
                ResultingList: new[] { -3 }, ErrorMessageKey: null));
        var ctrl = BuildSut(userA);

        var result = await ctrl.SaveBlockedDays(new List<int> { -3 }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        // Service receives A's id, not B's.
        await _trackingService.Received(1).SaveOwnBlockedDaysAsync(
            userA.Id, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _trackingService.DidNotReceive().SaveOwnBlockedDaysAsync(
            userB, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------
    // SaveBlockedDays — happy path
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SaveBlockedDays_HappyPath_RedirectsToMineWithSuccessTempData()
    {
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: true, Added: new[] { -3 }, Removed: Array.Empty<int>(),
                ResultingList: new[] { -3 }, ErrorMessageKey: null));
        var ctrl = BuildSut(current);

        var result = await ctrl.SaveBlockedDays(new List<int> { -3 }, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(ShiftsController.Mine));
        ctrl.TempData[TempDataKeys.SuccessMessage].Should().Be("VolTrack_Msg_BlockedDaysSaved");
    }

    [HumansFact]
    public async Task SaveBlockedDays_NullDayOffsets_PassesEmptyListToService()
    {
        // Empty selection (user unchecks all) posts no dayOffsets; binder
        // delivers null; controller normalizes to empty list.
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: true, Added: Array.Empty<int>(), Removed: Array.Empty<int>(),
                ResultingList: Array.Empty<int>(), ErrorMessageKey: null));
        var ctrl = BuildSut(current);

        await ctrl.SaveBlockedDays(dayOffsets: null, CancellationToken.None);

        await _trackingService.Received(1).SaveOwnBlockedDaysAsync(
            current.Id,
            Arg.Is<IReadOnlyList<int>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------
    // SaveBlockedDays — controller does not sort/dedup; service does.
    // The contract: the controller is a thin adapter over the service.
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SaveBlockedDays_UnsortedDuplicateOffsets_PassedThroughToService_Verbatim()
    {
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: true, Added: new[] { -5, -3 }, Removed: Array.Empty<int>(),
                ResultingList: new[] { -5, -3 }, ErrorMessageKey: null));
        var ctrl = BuildSut(current);
        var raw = new List<int> { -3, -5, -3, -5 }; // unsorted, duplicated

        await ctrl.SaveBlockedDays(raw, CancellationToken.None);

        // Sort/dedup is the service's job; the controller forwards verbatim.
        await _trackingService.Received(1).SaveOwnBlockedDaysAsync(
            current.Id,
            Arg.Is<IReadOnlyList<int>>(l =>
                l.Count == 4 && l[0] == -3 && l[1] == -5 && l[2] == -3 && l[3] == -5),
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------
    // SaveBlockedDays — service rejection
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SaveBlockedDays_ServiceRejects_RedirectsWithErrorTempData_NoAudit()
    {
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: false, Added: Array.Empty<int>(), Removed: Array.Empty<int>(),
                ResultingList: Array.Empty<int>(),
                ErrorMessageKey: "VolTrack_Err_DayOffsetOutsideBuild"));
        var ctrl = BuildSut(current);

        var result = await ctrl.SaveBlockedDays(new List<int> { 999 }, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        redirect.ActionName.Should().Be(nameof(ShiftsController.Mine));
        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_DayOffsetOutsideBuild");
        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SaveBlockedDays_ServiceRejects_NullErrorKey_FallsBackToUnknownError()
    {
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: false, Added: Array.Empty<int>(), Removed: Array.Empty<int>(),
                ResultingList: Array.Empty<int>(), ErrorMessageKey: null));
        var ctrl = BuildSut(current);

        await ctrl.SaveBlockedDays(new List<int> { -3 }, CancellationToken.None);

        ctrl.TempData[TempDataKeys.ErrorMessage].Should().Be("VolTrack_Err_Unknown");
    }

    // ---------------------------------------------------------------------
    // SaveBlockedDays — audit fan-out for bulk save.
    // Spec: one row per Added/Removed plus a single OwnBlockedDaysSaved row.
    // ---------------------------------------------------------------------

    [HumansFact]
    public async Task SaveBlockedDays_BulkSave_EmitsOneAuditPerDiff_PlusOneSummaryRow()
    {
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: true,
                Added: new[] { -3 },
                Removed: new[] { -7 },
                ResultingList: new[] { -3 },
                ErrorMessageKey: null));
        var ctrl = BuildSut(current);

        await ctrl.SaveBlockedDays(new List<int> { -3 }, CancellationToken.None);

        // One blocked row for the added offset.
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerDayBlocked,
            nameof(VolunteerBuildStatus),
            current.Id,
            Arg.Is<string>(s => s.Contains("DayOffset=-3") && s.Contains("self")),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        // One unblocked row for the removed offset.
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerDayUnblocked,
            nameof(VolunteerBuildStatus),
            current.Id,
            Arg.Is<string>(s => s.Contains("DayOffset=-7") && s.Contains("self")),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        // Single bulk-save summary row.
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerOwnBlockedDaysSaved,
            nameof(VolunteerBuildStatus),
            current.Id,
            Arg.Is<string>(s => s.Contains("Resulting list: [-3]")),
            current.Id,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        // Total = 3 audit calls (1 Added + 1 Removed + 1 Summary).
        await _auditLog.Received(3).LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SaveBlockedDays_NoChanges_StillEmitsOnlyTheSummaryRow()
    {
        // User submits an unchanged list. Service returns Added = Removed = [];
        // controller emits exactly one summary row (no per-day rows).
        var current = new User { Id = Guid.NewGuid() };
        _trackingService.SaveOwnBlockedDaysAsync(current.Id, Arg.Any<IReadOnlyList<int>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SaveOwnBlockedDaysResult(
                Ok: true, Added: Array.Empty<int>(), Removed: Array.Empty<int>(),
                ResultingList: new[] { -3 }, ErrorMessageKey: null));
        var ctrl = BuildSut(current);

        await ctrl.SaveBlockedDays(new List<int> { -3 }, CancellationToken.None);

        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.VolunteerDayBlocked,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.VolunteerDayUnblocked,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.VolunteerOwnBlockedDaysSaved,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}
