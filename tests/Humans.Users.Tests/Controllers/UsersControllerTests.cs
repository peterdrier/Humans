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

    [HumansFact]
    public async Task Map_InvalidStoredCoordinatesCannotBreakMarkerSerialization()
    {
        var users = Substitute.For<IUserService>();
        var valid = Located("Valid", UserState.Active, true);
        var north = Located("North", UserState.Active, true, 90, 180);
        var south = Located("South", UserState.Active, true, -90, -180);
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            valid, north, south,
            Located("NaN", UserState.Active, true, double.NaN, 0),
            Located("Infinity", UserState.Active, true, 0, double.PositiveInfinity),
            Located("Too far north", UserState.Active, true, 90.1, 0),
            Located("Too far west", UserState.Active, true, 0, -180.1)
        });
        var controller = new UsersController(users, Substitute.For<ITeamService>(),
            new ConfigurationBuilder().Build(), new ConfigurationRegistry(), NodaTime.SystemClock.Instance);

        var result = await controller.Map(TestContext.Current.CancellationToken);

        var markers = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<MapViewModel>().Subject.Markers;
        var serialize = () => System.Text.Json.JsonSerializer.Serialize(markers);
        serialize.Should().NotThrow();
        markers.Select(marker => marker.UserId).Should().BeEquivalentTo(new[] { valid.Id, north.Id, south.Id });
    }

    private static UserInfo Located(string name, UserState state, bool withLocation, double latitude = 40.4, double longitude = -3.7)
    {
        var id = Guid.NewGuid();
        var profile = new Profile { Id = Guid.NewGuid(), UserId = id, City = "Madrid" };
        if (withLocation)
        {
            profile.Latitude = latitude;
            profile.Longitude = longitude;
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
