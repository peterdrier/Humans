using Humans.Auth.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Web.Authorization;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Web.Tests.Authorization;

public class RoleAssignmentClaimsTransformationTests
{
    private readonly IRoleAssignmentService _roleAssignments;
    private readonly IUserService _userService;

    public RoleAssignmentClaimsTransformationTests()
    {
        _roleAssignments = Substitute.For<IRoleAssignmentService>();
        _roleAssignments
            .GetActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _userService = Substitute.For<IUserService>();
    }

    private RoleAssignmentClaimsTransformation BuildSut() =>
        new(_roleAssignments, _userService);

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
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
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
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(userId, state)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(userId));

        principal.HasClaim(RoleAssignmentClaimsTransformation.UserStateClaimType, state.ToString())
            .Should().BeTrue("the stored UserState must be stamped as the access claim");
        RoleAssignmentClaimsTransformation.GetUserState(principal).Should().Be(state);
        RoleAssignmentClaimsTransformation.IsActive(principal)
            .Should().Be(state == UserState.Active, "only Active grants full app access");

        await _userService.Received(1).GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Transform_stamps_Merged_for_a_tombstone_never_the_survivors_state()
    {
        // The cross-section read resolves a merge tombstone forward to its survivor; used here it would admit a
        // tombstone principal as the surviving member. The raw read keeps UserState.Merged.
        var tombstoneId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(tombstoneId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(tombstoneId, UserState.Merged)));
        _userService.GetUserInfoAsync(tombstoneId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(survivorId, UserState.Active)));

        var principal = await BuildSut().TransformAsync(BuildPrincipal(tombstoneId));

        RoleAssignmentClaimsTransformation.GetUserState(principal).Should().Be(UserState.Merged);
        RoleAssignmentClaimsTransformation.IsActive(principal)
            .Should().BeFalse("a merged account must never inherit the survivor's app access");
        await _userService.DidNotReceive().GetUserInfoAsync(tombstoneId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Transform_omits_UserState_claim_when_UserInfo_is_null()
    {
        var userId = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>())
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
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>())
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

    [HumansFact]
    public async Task Transform_reflects_a_state_or_role_change_on_the_very_next_request()
    {
        // No per-user claims cache: both inputs are caches their owners keep current, so changes show next request.
        var userId = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(
                new ValueTask<UserInfo?>(MakeUserInfo(userId, UserState.Active)),
                new ValueTask<UserInfo?>(MakeUserInfo(userId, UserState.Suspended)));
        IReadOnlyList<RoleAssignmentSnapshot> board = [new RoleAssignmentSnapshot("Board", ValidTo: null)];
        IReadOnlyList<RoleAssignmentSnapshot> none = [];
        _roleAssignments
            .GetActiveForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(board, none);
        var sut = BuildSut();

        var first = await sut.TransformAsync(BuildPrincipal(userId));
        var second = await sut.TransformAsync(BuildPrincipal(userId));

        RoleAssignmentClaimsTransformation.GetUserState(first).Should().Be(UserState.Active);
        first.HasClaim(ClaimTypes.Role, "Board").Should().BeTrue();
        RoleAssignmentClaimsTransformation.GetUserState(second).Should().Be(UserState.Suspended);
        second.HasClaim(ClaimTypes.Role, "Board").Should().BeFalse();
    }
}
