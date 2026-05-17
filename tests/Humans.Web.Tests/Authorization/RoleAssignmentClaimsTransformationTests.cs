using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="RoleAssignmentClaimsTransformation"/> sources
/// <c>IsSuspended</c> and <c>HasProfile</c> from the cached
/// <see cref="UserInfo"/> read-model (issue #741) rather than re-querying
/// <c>dbContext.Profiles</c> on every authenticated request.
/// </summary>
public class RoleAssignmentClaimsTransformationTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly IUserService _userService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;

    public RoleAssignmentClaimsTransformationTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);

        var services = new ServiceCollection();
        services.AddSingleton(_dbContext);
        _serviceProvider = services.BuildServiceProvider();

        _userService = Substitute.For<IUserService>();
        _clock = Substitute.For<IClock>();
        _clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 5, 17, 12, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
    }

    private RoleAssignmentClaimsTransformation BuildSut() =>
        new(_serviceProvider, _userService, _clock, _cache);

    private static ClaimsPrincipal BuildPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

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
    public async Task Transform_for_authenticated_user_never_reads_profiles_table()
    {
        var userId = Guid.NewGuid();

        // Plant a row in dbContext.Profiles whose State contradicts the
        // cached UserInfo. If the transformation read the profiles table,
        // the claim output would reflect this row instead of the UserInfo.
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Stale",
            FirstName = "Stale",
            LastName = "Row",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            State = ProfileState.Suspended, // contradicts UserInfo below
        });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // UserInfo says: profile present, NOT suspended.
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, hasProfile: true, isSuspended: false)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        // HasProfile claim reflects UserInfo (true), not "no profile".
        principal.HasClaim(
            RoleAssignmentClaimsTransformation.HasProfileClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue)
            .Should().BeTrue("HasProfile must be sourced from UserInfo.Profile");

        // IUserService was actually consulted.
        await _userService.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Transform_omits_ActiveMember_claim_when_UserInfo_reports_suspended()
    {
        var userId = Guid.NewGuid();

        // Put the user in the Volunteers team so the ActiveMember claim
        // would otherwise be granted.
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TeamId = SystemTeamIds.Volunteers,
            JoinedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        // User is on the Volunteers team — should produce ActiveMember claim
        // when not suspended (and null UserInfo is treated as not suspended).
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TeamId = SystemTeamIds.Volunteers,
            JoinedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

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
}
