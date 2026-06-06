using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="RoleAssignmentClaimsTransformation"/> stamps the user's stored
/// <see cref="UserState"/> as the access claim from cached <see cref="UserInfo.State"/>,
/// and adds role claims from <see cref="IRoleAssignmentService"/>.
/// </summary>
public class RoleAssignmentClaimsTransformationTests : IDisposable
{
    private readonly IRoleAssignmentService _roleAssignments;
    private readonly IUserServiceRead _userService;
    private readonly IMemoryCache _cache;

    public RoleAssignmentClaimsTransformationTests()
    {
        _roleAssignments = Substitute.For<IRoleAssignmentService>();
        _roleAssignments
            .GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _userService = Substitute.For<IUserServiceRead>();
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private RoleAssignmentClaimsTransformation BuildSut() =>
        new(_roleAssignments, _userService, _cache);

    private static ClaimsPrincipal BuildPrincipal(Guid userId)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private static UserInfo MakeUserInfo(Guid id, UserState state) =>
        UserInfo.Create(
            user: new User
            {
                Id = id,
                DisplayName = "Test User",
                PreferredLanguage = "en",
                State = state,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                GoogleEmailStatus = GoogleEmailStatus.Unknown,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    [HumansTheory]
    [InlineData(UserState.Active)]
    [InlineData(UserState.Bare)]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    [InlineData(UserState.DeletePending)]
    [InlineData(UserState.Rejected)]
    public async Task Transform_stamps_UserState_claim_matching_stored_state(UserState state)
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, state)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(RoleAssignmentClaimsTransformation.UserStateClaimType, state.ToString())
            .Should().BeTrue("the stored UserState must be stamped as the access claim");
        RoleAssignmentClaimsTransformation.GetUserState(principal).Should().Be(state);
        RoleAssignmentClaimsTransformation.IsActive(principal)
            .Should().Be(state == UserState.Active, "only Active grants full app access");

        await _userService.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Transform_omits_UserState_claim_when_UserInfo_is_null()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        RoleAssignmentClaimsTransformation.GetUserState(principal)
            .Should().BeNull("a null read-model leaves the principal without a UserState claim");
        RoleAssignmentClaimsTransformation.IsActive(principal).Should().BeFalse();
    }

    [HumansFact]
    public async Task Transform_adds_role_claims_from_auth_service_active_roles()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, UserState.Active)));

        _roleAssignments
            .GetActiveForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([
                new RoleAssignmentSnapshot("Board", ValidTo: null),
                new RoleAssignmentSnapshot("Treasurer", ValidTo: null),
            ]);

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(ClaimTypes.Role, "Board").Should().BeTrue();
        principal.HasClaim(ClaimTypes.Role, "Treasurer").Should().BeTrue();
    }
}
