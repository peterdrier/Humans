using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class CampControllerTests
{
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly ICampContactService _contacts = Substitute.For<ICampContactService>();
    private readonly ICampRoleService _roles = Substitute.For<ICampRoleService>();
    private readonly ICityPlanningService _cityPlanning = Substitute.For<ICityPlanningService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();

    [HumansFact]
    public async Task Index_BuildsPublicDirectory_FromCachedCampInfoRead()
    {
        var matching = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, kidsWelcome: YesNoMaybe.Yes);
        var filteredOut = MakeCamp("zeta", "Zeta Camp", CampSeasonStatus.Full, kidsWelcome: YesNoMaybe.No);
        var pending = MakeCamp("pending", "Pending Camp", CampSeasonStatus.Pending);
        StubCampReadModel([matching, filteredOut, pending]);
        var controller = BuildController();

        var result = await controller.Index(new CampFilterViewModel { KidsFriendly = true });

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Year.Should().Be(2026);
        vm.Camps.Should().ContainSingle(c => c.Id == matching.Id);
        vm.MyCamps.Should().BeEmpty();
        ((int)controller.ViewBag.PendingCount).Should().Be(1);
        await _camps.Received(1).GetSettingsAsync(Arg.Any<CancellationToken>());
        await _camps.Received(1).GetCampsForYearAsync(2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Index_RoleLeadPendingCamp_AppearsInMyCamps_FromCampInfo()
    {
        var userId = Guid.NewGuid();
        var pending = MakeCamp("pending", "Pending Camp", CampSeasonStatus.Pending, leadUserId: userId);
        StubCampReadModel([pending]);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId);

        var result = await controller.Index(null);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Should().BeEmpty();
        vm.MyCamps.Should().ContainSingle(c => c.Id == pending.Id);
        ((int)controller.ViewBag.PendingCount).Should().Be(1);
    }

    [HumansFact]
    public async Task Index_RoleLeadPublicCamp_IsPinnedBeforeAlphabeticalCamps()
    {
        var userId = Guid.NewGuid();
        var alphabeticalFirst = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active);
        var leadCamp = MakeCamp("zeta", "Zeta Camp", CampSeasonStatus.Active, leadUserId: userId);
        StubCampReadModel([alphabeticalFirst, leadCamp]);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId)));
        var controller = BuildController(userId);

        var result = await controller.Index(null);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Select(c => c.Id).Should().Equal(leadCamp.Id, alphabeticalFirst.Id);
    }

    private void StubCampReadModel(IReadOnlyList<CampInfo> camps)
    {
        _camps.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CampSettingsInfo(2026, [2026], null)));
        _camps.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(camps));
    }

    private CampController BuildController(Guid? userId = null)
    {
        var controller = new CampController(
            _camps,
            _contacts,
            _roles,
            _cityPlanning,
            _notifications,
            _users,
            _authorization,
            _clock,
            NullLogger<CampController>.Instance,
            _localizer);

        var services = new ServiceCollection();
        services.AddLogging();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (userId.HasValue)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())],
                authenticationType: "test"));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(CampController.Index) }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return controller;
    }

    private static CampInfo MakeCamp(
        string slug,
        string name,
        CampSeasonStatus status,
        Guid? leadUserId = null,
        YesNoMaybe kidsWelcome = YesNoMaybe.Yes)
    {
        var campId = Guid.NewGuid();
        var season = new CampSeasonInfo(
            Guid.NewGuid(),
            campId,
            slug,
            2026,
            NameLockDate: null,
            name,
            $"{name} short",
            Languages: "en",
            Vibes: [CampVibe.ChillOut],
            status,
            AcceptingMembers: YesNoMaybe.Yes,
            kidsWelcome,
            AdultPlayspacePolicy.No,
            MemberCount: 0,
            SoundZone: SoundZone.Green,
            SpaceRequirement: null,
            ElectricalGrid: null,
            EeSlotCount: 0,
            EeGrantedCount: 0,
            JoinedMemberCount: 0)
        {
            LeadUserIds = leadUserId.HasValue ? [leadUserId.Value] : []
        };

        return new CampInfo(
            campId,
            slug,
            ContactEmail: $"{slug}@example.com",
            ContactPhone: "+34600000000",
            IsSwissCamp: false,
            TimesAtNowhere: 1,
            Seasons: [season]);
    }

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            [],
            new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BurnerName = "Lead Human",
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
                UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
                State = ProfileState.Active,
                IsApproved = true
            },
            [],
            [],
            [],
            []);
}
