using AwesomeAssertions;
using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Interfaces.Users;
using Humans.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class ICalFeedApiControllerTests
{
    private readonly IICalFeedService _feed = Substitute.For<IICalFeedService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();

    private ICalFeedApiController CreateController() =>
        new(_feed, _users, NullLogger<ICalFeedApiController>.Instance);

    [HumansFact]
    public async Task GetFeed_ServiceReturnsNull_Returns404()
    {
        _feed.GetFeedIcsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await CreateController().GetFeed(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task GetFeed_ValidToken_ReturnsTextCalendarFile()
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        _feed.GetFeedIcsAsync(userId, token, Arg.Any<CancellationToken>())
            .Returns("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");

        var result = await CreateController().GetFeed(userId, token, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/calendar");
        System.Text.Encoding.UTF8.GetString(file.FileContents).Should().StartWith("BEGIN:VCALENDAR");
    }

    [HumansFact]
    public async Task GetFeed_ServiceThrows_Returns500()
    {
        _feed.GetFeedIcsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new InvalidOperationException("boom"));

        var result = await CreateController().GetFeed(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(500);
    }
}
