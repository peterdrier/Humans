using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Identity;

/// <summary>
/// Issue #635 (§15i, Phase 6 alt): verifies the
/// <see cref="LoggingUserStoreDecorator"/> emits a warning log on
/// <c>FindByEmailAsync</c> / <c>FindByNameAsync</c> calls and delegates to
/// the EF base store unchanged.
/// </summary>
public sealed class LoggingUserStoreDecoratorTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<LoggingUserStoreDecorator> _logger;
    private readonly LoggingUserStoreDecorator _store;

    public LoggingUserStoreDecoratorTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _logger = Substitute.For<ILogger<LoggingUserStoreDecorator>>();
        _logger.IsEnabled(LogLevel.Warning).Returns(true);
        _store = new LoggingUserStoreDecorator(_dbContext, _logger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _store.Dispose();
    }

    [HumansFact]
    public async Task FindByEmailAsync_LogsWarning_AndDelegatesToBase()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "u@example.com",
            NormalizedUserName = "U@EXAMPLE.COM",
            Email = "u@example.com",
            NormalizedEmail = "U@EXAMPLE.COM",
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var result = await _store.FindByEmailAsync("U@EXAMPLE.COM");

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Identity.FindByEmailAsync")
                && o.ToString()!.Contains("U@EXAMPLE.COM")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task FindByNameAsync_LogsWarning_AndDelegatesToBase()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "u@example.com",
            NormalizedUserName = "U@EXAMPLE.COM",
            Email = "u@example.com",
            NormalizedEmail = "U@EXAMPLE.COM",
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var result = await _store.FindByNameAsync("U@EXAMPLE.COM");

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Identity.FindByNameAsync")
                && o.ToString()!.Contains("U@EXAMPLE.COM")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [HumansFact]
    public async Task FindByEmailAsync_ReturnsNull_WhenUserNotFound()
    {
        var result = await _store.FindByEmailAsync("MISSING@EXAMPLE.COM");
        result.Should().BeNull();
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
