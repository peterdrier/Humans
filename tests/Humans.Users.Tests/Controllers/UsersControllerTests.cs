using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Models;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Controllers;

public class UsersControllerTests
{
    [HumansFact]
    public async Task Map_ListsEveryoneWithALocationExceptSuspended()
    {
        var users = Substitute.For<IUserService>();
        var active = Located("Unreviewed", UserState.Active, withLocation: true);
        var suspended = Located("Suspended", UserState.Suspended, withLocation: true);
        var nowhere = Located("Nowhere", UserState.Active, withLocation: false);
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new[] { active, suspended, nowhere });
        var controller = new UsersController(users, Substitute.For<ITeamService>(),
            new ConfigurationBuilder().Build(), new ConfigurationRegistry(), NodaTime.SystemClock.Instance);

        var result = await controller.Map(TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<MapViewModel>().Subject
            .Markers.Should().ContainSingle().Which.UserId.Should().Be(active.Id);
    }

    private static UserInfo Located(string name, UserState state, bool withLocation)
    {
        var id = Guid.NewGuid();
        var profile = new Profile { Id = Guid.NewGuid(), UserId = id, City = "Madrid" };
        if (withLocation)
        {
            profile.Latitude = 40.4;
            profile.Longitude = -3.7;
        }
        return UserInfoFactory.Create(
            user: new User { Id = id, BurnerName = name, DisplayName = name, State = state },
            userEmails: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: []);
    }
}
