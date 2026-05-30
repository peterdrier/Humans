using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Services.Camps;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Application.Tests.Services;

public class CampContactServiceTests : IDisposable
{
    private readonly IEmailService _emailService;
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly IAuditLogService _auditLogService;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly IMemoryCache _cache;
    private readonly CampContactService _service;

    private readonly Guid _campId = Guid.NewGuid();
    private readonly Guid _senderId = Guid.NewGuid();

    public CampContactServiceTests()
    {
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _notificationEmitter = Substitute.For<INotificationEmitter>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new CampContactService(
            _emailService,
            _emailMessages,
            _auditLogService,
            _notificationEmitter,
            _cache,
            NullLogger<CampContactService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
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
            includeContactInfo: false,
            [Guid.NewGuid()],
            "/Barrios/cool-camp");

        result.Success.Should().BeTrue();
        result.RateLimited.Should().BeFalse();

        _emailMessages.Received(1).FacilitatedMessage(
            "camp@example.com",
            "Cool Camp",
            "Alice",
            "Hello camp!",
            false,
            "alice@example.com");

        await _auditLogService.Received(1).LogAsync(
            Arg.Any<Humans.Domain.Enums.AuditAction>(),
            Arg.Any<string>(),
            _campId,
            Arg.Any<string>(),
            _senderId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_RateLimited_ReturnsFalse()
    {
        // First call succeeds
        var result1 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false, [], "/Barrios/camp");

        result1.Success.Should().BeTrue();

        // Second call within rate limit window is rejected
        var result2 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello again", false, [], "/Barrios/camp");

        result2.Success.Should().BeFalse();
        result2.RateLimited.Should().BeTrue();

        // Email only sent once
        _emailMessages.Received(1).FacilitatedMessage(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_DifferentCamp_NotRateLimited()
    {
        var otherCampId = Guid.NewGuid();

        var result1 = await _service.SendFacilitatedMessageAsync(
            _campId, "camp1@example.com", "Camp 1", _senderId,
            "Alice", "alice@example.com", "Hello 1", false, [], "/Barrios/camp-1");

        var result2 = await _service.SendFacilitatedMessageAsync(
            otherCampId, "camp2@example.com", "Camp 2", _senderId,
            "Alice", "alice@example.com", "Hello 2", false, [], "/Barrios/camp-2");

        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_EmailFails_RollsBackRateLimit()
    {
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP error"));

        var act = () => _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false, [], "/Barrios/camp");

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Rate limit should be rolled back, so next attempt should not be rate-limited
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var retryResult = await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com", "Hello", false, [], "/Barrios/camp");

        retryResult.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task SendFacilitatedMessageAsync_SanitizesHtmlFromMessage()
    {
        await _service.SendFacilitatedMessageAsync(
            _campId, "camp@example.com", "Camp", _senderId,
            "Alice", "alice@example.com",
            "Hello <script>alert('xss')</script> world",
            false,
            [],
            "/Barrios/camp");

        _emailMessages.Received(1).FacilitatedMessage(
            "camp@example.com",
            "Camp",
            "Alice",
            "Hello alert('xss') world",
            false,
            "alice@example.com");
    }
}
