using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Models;
using Humans.Backdoor.Services;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The section's one page, at <c>/Backdoor</c>. Covers the one thing it decides on its own:
/// which humans the allocate dropdown offers (nobodies-collective/Humans#1128).
/// </summary>
public class BackdoorControllerTests
{
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
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
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [_activeAdmin] = Info(_activeAdmin, UserState.Active),
                    [_suspendedAdmin] = Info(_suspendedAdmin, UserState.AdminSuspended),
                }));

        _sut = new BackdoorController(_keys, _roles, _users)
        {
            TempData = new TempDataDictionary(
                new DefaultHttpContext(), Substitute.For<ITempDataProvider>()),
        };
    }

    [HumansFact]
    public async Task Allocate_dropdown_omits_a_role_holder_whose_account_is_suspended()
    {
        var result = await _sut.Index(Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeOfType<BackdoorKeysViewModel>().Subject;

        // A suspension leaves the Admin assignment standing, so the role set still names them
        // — but IsEligibleAsync would refuse the issue, and the dropdown must not offer it.
        model.EligibleUsers.Select(u => u.UserId).Should().ContainSingle()
            .Which.Should().Be(_activeAdmin);
    }

    private static UserInfo Info(Guid id, UserState state) => UserInfo.Create(
        new User { Id = id, State = state, PreferredLanguage = "en" },
        [], [], [], profile: null, []);
}
