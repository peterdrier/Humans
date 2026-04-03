using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampContactServiceTests : IDisposable
{
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMemoryCache _cache;
    private readonly CampContactService _service;

    private readonly Guid _campId = Guid.NewGuid();
    private readonly Guid _senderId = Guid.NewGuid();

    public CampContactServiceTests()
    {
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new CampContactService(
            _emailService,
            _auditLogService,
            _cache,
            NullLogger<CampContactService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_SuccessfulSend_ReturnsSuccess()
    {
        var result = await _service.SendFacilitatedMessageAsync(
            _campId,
            "camp@example.com",
            "Cool Camp",
            _senderId,
            "Alice",
            "alice@example.com",
            "Hello camp!",
            includeContactInfo: false);

        result.Success.Should().BeTrue();
        result.RateLimited.Should().BeFalse();

        await _emailService.Received(1).SendFacilitatedMessageAsync(
            "camp@example.com",
            "Cool Camp",
            "Alice",
            "Hello camp!",
            false,
            "alice@example.com");

        await _auditLogService.Received(1).LogAsync(
            Arg.Any<Domain.Enums.AuditAction>(),
            Arg.Any<string>(),
            _campId,
            Arg.Any<string>(),
            _senderId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_RateLimited_ReturnsFalse()
    {
        // First call succeeds
        var result1 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false);

        result1.Success.Should().BeTrue();

        // Second call within rate limit window is rejected
        var result2 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello again", false);

        result2.Success.Should().BeFalse();
        result2.RateLimited.Should().BeTrue();

        // Email only sent once
        await _emailService.Received(1).SendFacilitatedMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_DifferentCamp_NotRateLimited()
    {
        var otherCampId = Guid.NewGuid();

        var result1 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp1@example.com", "Camp 1", _senderId,
            "Alice", "alice@example.com", "Hello 1", false);

        var result2 = await _service.SendFacilitatedMessageAsync(
            otherCampId, "camp2@example.com", "Camp 2", _senderId,
            "Alice", "alice@example.com", "Hello 2", false);

        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_EmailFails_RollsBackRateLimit()
    {
        _emailService.SendFacilitatedMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("SMTP error"));

        var act = () => _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Rate limit should be rolled back, so next attempt should not be rate-limited
        _emailService.SendFacilitatedMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var retryResult = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false);

        retryResult.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SendFacilitatedMessageAsync_SanitizesHtmlFromMessage()
    {
        await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com",
            "Hello <script>alert('xss')</script> world",
            false);

        await _emailService.Received(1).SendFacilitatedMessageAsync(
            "camp@example.com",
            "Camp",
            "Alice",
            "Hello alert('xss') world",
            false,
            "alice@example.com");
    }
}
