using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="RoleAssignmentClaimsTransformation"/> sources
/// every claim it emits through the application service / repository surface
/// — never <c>HumansDbContext</c> directly (issue #750). HasProfile /
/// IsSuspended come from the cached <see cref="UserInfo"/> read-model (issue
/// #741), role claims come from <see cref="IRoleAssignmentRepository"/>, and
/// Volunteers-team membership comes from <see cref="ITeamService"/> (cache-
/// backed).
/// </summary>
public class RoleAssignmentClaimsTransformationTests : IDisposable
{
    private readonly IRoleAssignmentRepository _roleAssignments;
    private readonly ITeamService _teams;
    private readonly IUserService _userService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    public RoleAssignmentClaimsTransformationTests()
    {
        _roleAssignments = Substitute.For<IRoleAssignmentRepository>();
        _roleAssignments
            .GetActiveRoleNamesAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _teams = Substitute.For<ITeamService>();
        _teams
            .GetUserTeamsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _userService = Substitute.For<IUserService>();
        _clock = Substitute.For<IClock>();
        _clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 5, 17, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private RoleAssignmentClaimsTransformation BuildSut() =>
        new(_roleAssignments, _teams, _userService, _clock, _cache);

    private static ClaimsPrincipal BuildPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private static TeamMember VolunteersMembership(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TeamId = SystemTeamIds.Volunteers,
        JoinedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static UserInfo MakeUserInfo(Guid id, bool hasProfile, bool isSuspended)
    {
        Profile? profile = hasProfile
            ? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = id,
                BurnerName = "Burner",
                FirstName = "First",
                LastName = "Last",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                State = isSuspended ? ProfileState.Suspended : ProfileState.Active,
            }
            : null;

        return UserInfo.Create(
            user: new User
            {
                Id = id,
                DisplayName = "Test User",
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }

    [HumansFact]
    public async Task Transform_sources_HasProfile_from_UserInfo()
    {
        var userId = Guid.NewGuid();

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, hasProfile: true, isSuspended: false)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(
            RoleAssignmentClaimsTransformation.HasProfileClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue)
            .Should().BeTrue("HasProfile must be sourced from UserInfo.Profile");

        await _userService.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Transform_omits_ActiveMember_claim_when_UserInfo_reports_suspended()
    {
        var userId = Guid.NewGuid();

        // The team service would say the user IS on the Volunteers team —
        // but suspension wins and the claim must be omitted.
        _teams
            .GetUserTeamsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([VolunteersMembership(userId)]);

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, hasProfile: true, isSuspended: true)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(
            RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue)
            .Should().BeFalse("suspended users lose the ActiveMember claim");
    }

    [HumansFact]
    public async Task Transform_omits_HasProfile_claim_when_UserInfo_has_no_profile()
    {
        var userId = Guid.NewGuid();

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, hasProfile: false, isSuspended: false)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(
            RoleAssignmentClaimsTransformation.HasProfileClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue)
            .Should().BeFalse("profileless UserInfo should not emit HasProfile");
    }

    [HumansFact]
    public async Task Transform_treats_null_UserInfo_as_no_profile_and_not_suspended()
    {
        var userId = Guid.NewGuid();

        _teams
            .GetUserTeamsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([VolunteersMembership(userId)]);

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(
            RoleAssignmentClaimsTransformation.HasProfileClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue)
            .Should().BeFalse("null UserInfo means no profile");

        principal.HasClaim(
            RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue)
            .Should().BeTrue("null UserInfo is not suspended; volunteer team membership still grants ActiveMember");
    }

    [HumansFact]
    public async Task Transform_adds_role_claims_from_repository_active_role_names()
    {
        var userId = Guid.NewGuid();

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, hasProfile: true, isSuspended: false)));

        _roleAssignments
            .GetActiveRoleNamesAsync(userId, Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(["Board", "Treasurer"]);

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(ClaimTypes.Role, "Board").Should().BeTrue();
        principal.HasClaim(ClaimTypes.Role, "Treasurer").Should().BeTrue();
    }
}
