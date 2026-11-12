using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Shifts.Contracts;
using Humans.Shifts.Models;
using Humans.Shifts.Services;
using Humans.Shifts.ViewComponents;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Domain;
using Humans.Users.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

/// <summary>Another member's shift card on their profile: who sees it, and the no-show order.</summary>
public sealed class ProfileShiftsViewComponentTests
{
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftService _shifts = Substitute.For<IShiftService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly Guid _viewerId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();

    public ProfileShiftsViewComponentTests()
    {
        _users.GetUserInfoAsync(_viewerId, Arg.Any<CancellationToken>()).Returns(MakeUserInfo(_viewerId));
        _shifts.GetCoordinatorTeamIdsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([]);
        _signups.GetNoShowHistoryAsync(_targetId, Arg.Any<CancellationToken>()).Returns([]);
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
    }

    [HumansTheory]
    [Xunit.InlineData(ProfileCardViewMode.Self, true, false)]
    [Xunit.InlineData(ProfileCardViewMode.Admin, false, false)]
    [Xunit.InlineData(ProfileCardViewMode.Admin, true, true)]
    [Xunit.InlineData(ProfileCardViewMode.Public, false, true)]
    public async Task Shows_only_to_a_coordinator_or_privileged_approver_on_someone_elses_profile(
        ProfileCardViewMode viewMode, bool coordinator, bool shown)
    {
        if (coordinator)
            _shifts.GetCoordinatorTeamIdsAsync(_viewerId, Arg.Any<CancellationToken>()).Returns([Guid.NewGuid()]);
        // The Public + not-coordinator row is the privileged approver.
        var sut = Build(privileged: viewMode == ProfileCardViewMode.Public);

        var result = await sut.InvokeAsync(_targetId, viewMode);

        if (shown) result.Should().BeOfType<ViewViewComponentResult>();
        else result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Orders_no_shows_newest_review_first_and_preserves_cancellation()
    {
        using var source = new CancellationTokenSource();
        var reviewed = Instant.FromUtc(2026, 3, 1, 12, 0);
        var reviewerId = Guid.NewGuid();
        _signups.GetNoShowHistoryAsync(_targetId, Arg.Any<CancellationToken>()).Returns(
        [
            new NoShowHistoryEntry("Undated", Guid.Empty, reviewed, "UTC", null, null),
            new NoShowHistoryEntry("Older", Guid.Empty, reviewed, "UTC", reviewerId, reviewed),
            new NoShowHistoryEntry("Newest", Guid.Empty, reviewed, "UTC", reviewerId, reviewed + Duration.FromHours(1)),
        ]);
        var sut = Build(privileged: true);
        sut.HttpContext.RequestAborted = source.Token;

        var result = await sut.InvokeAsync(_targetId, ProfileCardViewMode.Admin);

        var model = (ProfileShiftsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.NoShowHistory.Select(r => r.ShiftLabel).Should().Equal("Newest", "Older", "Undated");
        await _signups.Received(1).GetNoShowHistoryAsync(_targetId, source.Token);
    }

    private ProfileShiftsViewComponent Build(bool privileged)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, _viewerId.ToString()) };
        if (privileged) claims.Add(new Claim(ClaimTypes.Role, ShiftsRoles.ShiftsNoInfo));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new ProfileShiftsViewComponent(_signups, _shifts, _teams, _users)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new ViewContext { HttpContext = http } },
        };
    }

    private static UserInfo MakeUserInfo(Guid userId) => UserInfoFactory.Create(
        user: new User { Id = userId, DisplayName = "Viewer", PreferredLanguage = "en" },
        userEmails: [],
        externalLogins: [],
        profile: new Profile { UserId = userId, BurnerName = "Viewer" },
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: []);
}
