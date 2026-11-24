using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Models;
using Humans.Backdoor.Services;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using NodaTime;
using System.Security.Claims;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The section's one page, at <c>/Backdoor</c>. Covers what it decides on its own: which
/// humans the allocate dropdown offers, and whether a listed key actually works.
/// </summary>
public class BackdoorControllerTests
{
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();
    private readonly ISystemRoleService _roles = Substitute.For<ISystemRoleService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuthorizationService _auth = Substitute.For<IAuthorizationService>();
    private readonly BackdoorController _sut;

    private readonly Guid _activeAdmin = Guid.NewGuid();
    private readonly Guid _suspendedAdmin = Guid.NewGuid();

    public BackdoorControllerTests()
    {
        _keys.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, Arg.Any<CancellationToken>())
            .Returns([_activeAdmin, _suspendedAdmin]);
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns([]);
        _users.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [_activeAdmin] = Info(_activeAdmin, UserState.Active),
                    [_suspendedAdmin] = Info(_suspendedAdmin, UserState.AdminSuspended),
                }));

        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<UserInfo?>(Info(ci.Arg<Guid>(), UserState.Active)));

        _sut = new BackdoorController(_keys, _roles, _users, _auth, Substitute.For<IStringLocalizer<BackdoorResource>>())
        {
            TempData = new TempDataDictionary(
                new DefaultHttpContext(), Substitute.For<ITempDataProvider>()),
        };
        SignIn(_activeAdmin, RoleNames.Admin);
    }

    [HumansFact]
    public async Task Someone_below_Board_sees_only_their_own_keys_and_no_owner_picker()
    {
        var me = Guid.NewGuid();
        SignIn(me);
        _keys.IsEligibleAsync(me, Arg.Any<CancellationToken>()).Returns(true);
        var mine = Guid.NewGuid();
        _keys.ListAsync(Arg.Any<CancellationToken>()).Returns([
            Row(mine, me, ownerEligible: true),
            Row(Guid.NewGuid(), _activeAdmin, ownerEligible: true),
        ]);

        var model = await IndexModelAsync();

        model.Keys.Should().ContainSingle().Which.Id.Should().Be(mine);
        model.ShowsOwners.Should().BeFalse();
        model.EligibleUsers.Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_Board_member_cannot_revoke_someone_elses_key()
    {
        var me = Guid.NewGuid();
        SignIn(me, RoleNames.Board);
        var theirs = Guid.NewGuid();
        _keys.ListAsync(Arg.Any<CancellationToken>()).Returns([Row(theirs, _activeAdmin, ownerEligible: true)]);

        await _sut.Revoke(theirs, Xunit.TestContext.Current.CancellationToken);

        await _keys.DidNotReceiveWithAnyArgs().RevokeAsync(default, default);
    }

    [HumansFact]
    public async Task A_Board_member_cannot_make_a_key_for_someone_else()
    {
        SignIn(Guid.NewGuid(), RoleNames.Board);

        await _sut.Issue(_activeAdmin, "laptop", Xunit.TestContext.Current.CancellationToken);

        await _keys.DidNotReceiveWithAnyArgs().IssueAsync(default, default!, default);
    }

    [HumansFact]
    public async Task Someone_no_scope_admits_is_turned_away()
    {
        var me = Guid.NewGuid();
        SignIn(me);
        _keys.IsEligibleAsync(me, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.Index(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
    }

    private void SignIn(Guid userId, params string[] roles)
    {
        Allow(PolicyNames.AdminOnly, roles.Contains(RoleNames.Admin));
        Allow(PolicyNames.BoardOrAdmin, roles.Contains(RoleNames.Admin) || roles.Contains(RoleNames.Board));
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
                    "test")),
            },
        };
    }

    private void Allow(string policy, bool allowed) =>
        _auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), policy)
            .Returns(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    [HumansFact]
    public async Task A_key_whose_owner_is_no_longer_eligible_is_listed_as_unusable()
    {
        var goodKey = Guid.NewGuid();
        var strandedKey = Guid.NewGuid();
        _keys.ListAsync(Arg.Any<CancellationToken>()).Returns([
            Row(goodKey, _activeAdmin, ownerEligible: true),
            Row(strandedKey, _suspendedAdmin, ownerEligible: false),
        ]);

        var model = await IndexModelAsync();

        // Neither row is revoked, but the service flags the suspended owner's key as one
        // ResolveOwnerAsync refuses — the page has to say so rather than badge it green.
        model.Keys.Single(k => k.Id == goodKey).IsUsable.Should().BeTrue();
        var stranded = model.Keys.Single(k => k.Id == strandedKey);
        stranded.IsRevoked.Should().BeFalse();
        stranded.IsUsable.Should().BeFalse();
    }

    [HumansFact]
    public async Task Allocate_dropdown_omits_a_role_holder_whose_account_is_suspended()
    {
        var model = await IndexModelAsync();

        // A suspension leaves the Admin assignment standing, so the role set still names them
        // — but IsEligibleAsync would refuse the issue, and the dropdown must not offer it.
        model.EligibleUsers.Select(u => u.UserId).Should().ContainSingle()
            .Which.Should().Be(_activeAdmin);
    }

    private async Task<BackdoorKeysViewModel> IndexModelAsync()
    {
        var result = await _sut.Index(Xunit.TestContext.Current.CancellationToken);

        return result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<BackdoorKeysViewModel>().Subject;
    }

    private static BackdoorKeyRow Row(Guid id, Guid ownerId, bool ownerEligible) => new(
        id, ownerId, "laptop", "hum_abcd", Instant.FromUnixTimeSeconds(0), null, null, null, ownerEligible, false);

    private static UserInfo Info(Guid id, UserState state) => UserInfo.Create(
        new User { Id = id, State = state, PreferredLanguage = "en" },
        [], [], profile: null);
}
