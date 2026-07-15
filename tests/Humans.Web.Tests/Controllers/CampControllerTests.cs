using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Services.Camps;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Shifts;
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
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
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

        var result = await controller.Index(new CampFilterViewModel { KidsFriendly = true }, Xunit.TestContext.Current.CancellationToken);

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
    public async Task Index_InvalidFilterQueryValue_ClearsModelStateAndRendersWithDefaults()
    {
        // Regression for nobodies-collective/Humans#926: a scanner payload in a bool
        // filter param leaves an invalid attempted value in ModelState, which the
        // checkbox tag helper would throw a FormatException re-converting at render.
        StubCampReadModel([]);
        var controller = BuildController();
        const string garbage = "test\")))EXTRACTVALUE(7966,...)";
        controller.ModelState.SetModelValue(
            "ShowLeadPositions", new Microsoft.AspNetCore.Mvc.ModelBinding.ValueProviderResult(garbage));
        controller.ModelState.AddModelError(
            "ShowLeadPositions", $"The value '{garbage}' is not valid for ShowLeadPositions.");

        var result = await controller.Index(new CampFilterViewModel(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
        controller.ModelState.Should().BeEmpty(
            "the invalid attempted value must not survive to view rendering");
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

        var result = await controller.Index(null, Xunit.TestContext.Current.CancellationToken);

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

        var result = await controller.Index(null, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampIndexViewModel>().Subject;
        vm.Camps.Select(c => c.Id).Should().Equal(leadCamp.Id, alphabeticalFirst.Id);
    }

    [HumansFact]
    public async Task Members_Shows_Active_Event_Shift_Counts_From_Shift_View()
    {
        var leadUserId = Guid.NewGuid();
        var zeroShiftMemberId = Guid.NewGuid();
        var oneShiftMemberId = Guid.NewGuid();
        var fivePlusShiftMemberId = Guid.NewGuid();

        var members = new[]
        {
            MakeSeasonMember(zeroShiftMemberId),
            MakeSeasonMember(oneShiftMemberId),
            MakeSeasonMember(fivePlusShiftMemberId),
        };
        var camp = MakeCamp("garden-of-joy", "Garden of Joy", CampSeasonStatus.Active, leadUserId, members: members);
        var season = camp.Seasons.Single();

        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _camps.GetCampEditDataAsync(camp.Id, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampEditData?>(MakeEditData(camp, season)));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                call.ArgAt<IReadOnlyCollection<Guid>>(0).ToDictionary(id => id, MakeUserInfo)));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
        _roles.BuildPanelAsync(season.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CampRolesPanelData(season.Id, [])));
        _shiftView.GetUsersAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>>(
                new Dictionary<Guid, ShiftUserView>
                {
                    [zeroShiftMemberId] = MakeShiftUserView(zeroShiftMemberId),
                    [oneShiftMemberId] = MakeShiftUserView(oneShiftMemberId, SignupStatus.Confirmed),
                    [fivePlusShiftMemberId] = MakeShiftUserView(
                        fivePlusShiftMemberId,
                        SignupStatus.Confirmed,
                        SignupStatus.Pending,
                        SignupStatus.Confirmed,
                        SignupStatus.Pending,
                        SignupStatus.Confirmed,
                        SignupStatus.Bailed),
                }));

        var controller = BuildController(leadUserId);

        var result = await controller.Members(camp.Slug, null, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<CampEditViewModel>().Subject;
        vm.ActiveMembers.Single(m => m.UserId == zeroShiftMemberId).EventShiftSignupCount.Should().Be(0);
        vm.ActiveMembers.Single(m => m.UserId == oneShiftMemberId).EventShiftSignupCount.Should().Be(1);
        vm.ActiveMembers.Single(m => m.UserId == fivePlusShiftMemberId).EventShiftSignupCount.Should().Be(5);
        vm.ActiveMembers.Single(m => m.UserId == fivePlusShiftMemberId).EventShiftSignupDisplay.Should().Be("5+");
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
            _shiftView,
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
        YesNoMaybe kidsWelcome = YesNoMaybe.Yes,
        IReadOnlyList<CampSeasonMemberInfo>? members = null)
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
            LeadUserIds = leadUserId.HasValue ? [leadUserId.Value] : [],
            Members = members ?? []
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

    private static CampSeasonMemberInfo MakeSeasonMember(Guid userId) =>
        new(
            Guid.NewGuid(),
            userId,
            CampMemberStatus.Active,
            SystemClock.Instance.GetCurrentInstant(),
            SystemClock.Instance.GetCurrentInstant(),
            HasEarlyEntry: false);

    private static CampEditData MakeEditData(CampInfo camp, CampSeasonInfo season) =>
        new(
            camp.Id,
            camp.Slug,
            season.Id,
            season.Year,
            IsNameLocked: false,
            season.Name,
            camp.ContactEmail,
            camp.ContactPhone,
            Links: [],
            camp.IsSwissCamp,
            camp.HideHistoricalNames,
            camp.TimesAtNowhere,
            season.BlurbLong,
            season.BlurbShort,
            season.Languages,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.KidsVisiting,
            season.KidsAreaDescription,
            season.HasPerformanceSpace,
            season.PerformanceTypes,
            season.Vibes,
            season.AdultPlayspace,
            season.MemberCount,
            season.SpaceRequirement,
            season.SoundZone,
            season.ElectricalGrid,
            Images: [],
            HistoricalNames: []);

    private static ShiftUserView MakeShiftUserView(Guid userId, params SignupStatus[] statuses) =>
        new(
            userId,
            Profile: null,
            Availability: null,
            BuildStatus: null,
            TagPreferences: [],
            Signups: statuses.Select(status => new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = Guid.NewGuid(),
                Status = status,
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
                UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
            }).ToList());

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
