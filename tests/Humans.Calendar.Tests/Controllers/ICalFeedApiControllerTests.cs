using AwesomeAssertions;
using Humans.Calendar.Services;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;
using Humans.Calendar.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Calendar.Tests.Controllers;

public class ICalFeedApiControllerTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();

    private ICalFeedApiController CreateController(ICalendarFeedTokenService? tokens = null) =>
        new(new ICalFeedService(_users, tokens ?? Substitute.For<ICalendarFeedTokenService>(), [],
                Options.Create(new EmailSettings { BaseUrl = "https://calendar.example" }),
                NullLogger<ICalFeedService>.Instance),
            _users, NullLogger<ICalFeedApiController>.Instance);

    [HumansFact]
    public async Task GetFeed_ServiceReturnsNull_Returns404()
    {
        var result = await CreateController().GetFeed(Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task GetFeed_ValidToken_ReturnsTextCalendarFile()
    {
        var userId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var user = UserInfo.Create(new User { Id = userId }, [], [], null);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(user));
        var tokens = Substitute.For<ICalendarFeedTokenService>();
        tokens.GetAsync(userId, Arg.Any<CancellationToken>()).Returns((Guid?)token);
        var result = await CreateController(tokens).GetFeed(userId, token, Xunit.TestContext.Current.CancellationToken);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/calendar");
        System.Text.Encoding.UTF8.GetString(file.FileContents).Should().StartWith("BEGIN:VCALENDAR");
    }

    [HumansFact]
    public async Task GetFeed_ServiceThrows_Returns500()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<UserInfo?>(new InvalidOperationException("boom")));

        var result = await CreateController().GetFeed(Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(500);
    }

    [HumansFact]
    public async Task GetFeed_CancelledRequest_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _users.GetUserInfoAsync(Arg.Any<Guid>(), cancellation.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(cancellation.Token)));

        var act = () => CreateController().GetFeed(Guid.NewGuid(), Guid.NewGuid(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
