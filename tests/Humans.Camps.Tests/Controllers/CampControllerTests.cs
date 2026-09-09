using System.Security.Claims;
using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Enums;
using Humans.Base;
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
using Humans.Users.Contracts;

namespace Humans.Camps.Tests.Controllers;

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
    private readonly IStringLocalizer<CampsResource> _campsLocalizer = Substitute.For<IStringLocalizer<CampsResource>>();
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();

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
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>>(
                new Dictionary<Guid, ShiftUserSummary>
                {
                    [zeroShiftMemberId] = MakeShiftUserSummary(zeroShiftMemberId),
                    [oneShiftMemberId] = MakeShiftUserSummary(oneShiftMemberId, SignupStatus.Confirmed),
                    [fivePlusShiftMemberId] = MakeShiftUserSummary(
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

    [HumansFact]
    public async Task Details_NonPublicSeason_AnonymousViewer_IsRefused()
    {
        // Destination-page half of the nobodies-collective/Humans#985 ruling: global search
        // stops offering a camp once its public-year season leaves Active/Full, and the id
        // path deliberately does not — so /Camps/{slug} has to be the enforcement point
        // (nobodies-collective/Humans#993).
        var pending = MakeCamp("pending-camp", "Pending Camp", CampSeasonStatus.Pending);
        StubCampReadModel([pending]);
        _camps.GetCampBySlugAsync(pending.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(pending));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().Details(pending.Slug, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task SeasonDetails_NonPublicSeason_AnonymousViewer_IsRefused()
    {
        // Equivalent nobodies-collective/Humans#993 coverage for the arbitrary-year route.
        var withdrawn = MakeCamp("withdrawn-camp", "Withdrawn Camp", CampSeasonStatus.Withdrawn);
        StubCampReadModel([withdrawn]);
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().SeasonDetails(withdrawn.Slug, 2026, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Details_NonPublicSeason_Lead_StillRenders()
    {
        // The #993 gate must not lock a lead out of their own Pending camp.
        var leadUserId = Guid.NewGuid();
        var pending = MakeCamp("pending-camp", "Pending Camp", CampSeasonStatus.Pending, leadUserId: leadUserId);
        StubCampReadModel([pending]);
        _camps.GetCampBySlugAsync(pending.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(pending));
        _users.GetUserInfoAsync(leadUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(leadUserId)));
        _cityPlanning.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CityPlanningSettingsDto(
                Guid.NewGuid(), 2026, false, null, null, null, null, null, null, null, false, null, null,
                Instant.FromUtc(2026, 1, 1, 0, 0))));
        _roles.BuildPanelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new CampRolesPanelData(call.ArgAt<Guid>(0), [])));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var result = await BuildController(leadUserId).Details(pending.Slug, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Details_AnonymousRender_CarriesNoMemberOrEarlyEntryData()
    {
        // Behavioral pin for "membership/EE data never render publicly": walk the actual
        // anonymous view-model graph — a leak that is renamed or nested still shows up
        // as the member's id or an Ee*/EarlyEntry-named node somewhere in the graph.
        var memberUserId = Guid.NewGuid();
        var member = new CampSeasonMemberInfo(
            Guid.NewGuid(), memberUserId, CampMemberStatus.Active,
            Instant.FromUtc(2026, 1, 1, 0, 0), Instant.FromUtc(2026, 1, 2, 0, 0),
            HasEarlyEntry: true);
        var camp = MakeCamp("alpha", "Alpha Camp", CampSeasonStatus.Active, members: [member]);
        StubCampReadModel([camp]);
        _camps.GetCampBySlugAsync(camp.Slug, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CampInfo?>(camp));
        _authorization.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await BuildController().Details(camp.Slug, Xunit.TestContext.Current.CancellationToken);

        var vm = result.Should().BeOfType<ViewResult>().Subject.Model!;
        var leaves = new List<(string Path, object? Value)>();
        CollectLeaves(vm, "vm", leaves, []);

        leaves.Should().NotContain(
            l => Equals(l.Value, memberUserId) || memberUserId.ToString().Equals(l.Value as string, StringComparison.OrdinalIgnoreCase),
            because: "an anonymous detail render must not carry any member identity");
        leaves.Where(l => l.Path.Split('.', '[')
                .Any(seg => seg.StartsWith("Ee", StringComparison.Ordinal)
                            || seg.Contains("EarlyEntry", StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty("Early Entry state is admin-only (issue #490, spec §4.4) and must not reach the public detail shape");
    }

    private static void CollectLeaves(object? obj, string path, List<(string Path, object? Value)> leaves, HashSet<object> seen)
    {
        if (obj is null)
        {
            return;
        }

        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum || obj is string || obj is Guid || obj is decimal
            || type.Namespace?.StartsWith("NodaTime", StringComparison.Ordinal) == true)
        {
            leaves.Add((path, obj));
            return;
        }

        if (obj is System.Collections.IEnumerable enumerable)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                CollectLeaves(item, $"{path}[{i++}]", leaves, seen);
            }

            return;
        }

        if (!seen.Add(obj))
        {
            return;
        }

        foreach (var property in type.GetProperties().Where(p => p.GetIndexParameters().Length == 0))
        {
            CollectLeaves(property.GetValue(obj), $"{path}.{property.Name}", leaves, seen);
        }
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
            _campsLocalizer,
            _sharedLocalizer);

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

    private static ShiftUserSummary MakeShiftUserSummary(Guid userId, params SignupStatus[] statuses) =>
        ShiftFixtures.UserSummary(
            userId,
            [.. statuses.Select(status => ShiftFixtures.Signup(status: status))]);

    private static UserInfo MakeUserInfo(Guid userId) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            [],
            [],
            [],
            UserFixtures.Profile(
                burnerName: "Lead Human",
                isApproved: true),
            []);
}
