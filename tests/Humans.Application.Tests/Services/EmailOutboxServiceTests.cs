using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.SystemSettings;
using Humans.Application.Services.Email;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class EmailOutboxServiceTests
{
    private readonly Instant _now = Instant.FromUtc(2026, 5, 10, 12, 0);
    private readonly IEmailOutboxRepository _repo = Substitute.For<IEmailOutboxRepository>();
    private readonly ISystemSettingsService _systemSettings = Substitute.For<ISystemSettingsService>();
    private readonly EmailOutboxService _service;

    public EmailOutboxServiceTests()
    {
        _service = new EmailOutboxService(_repo, _systemSettings, new FakeClock(_now));
    }

    [HumansFact]
    public async Task GetOutboxStatsAsync_ReturnsNewestRecentMessages()
    {
        var middle = BuildMessage(_now - Duration.FromHours(1));
        var newer = BuildMessage(_now);

        _repo.GetRecentAsync(2, Arg.Any<CancellationToken>())
            .Returns([newer, middle]);

        var result = await _service.GetOutboxStatsAsync(recentMessageCount: 2);

        result.RecentMessages.Select(m => m.Id).Should().Equal(newer.Id, middle.Id);
    }

    [HumansFact]
    public async Task GetMessagesForUserAsync_ReturnsNewestFirst()
    {
        var userId = Guid.NewGuid();
        var older = BuildMessage(_now - Duration.FromHours(2), userId);
        var newer = BuildMessage(_now - Duration.FromHours(1), userId);

        _repo.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([newer, older]);

        var result = await _service.GetMessagesForUserAsync(userId);

        result.Select(m => m.Id).Should().Equal(newer.Id, older.Id);
    }

    [HumansFact]
    public async Task IsEmailPausedAsync_ReturnsTrueWhenSettingIsTrue()
    {
        _systemSettings.GetValueAsync(
                SystemSettingKeys.IsEmailSendingPaused,
                Arg.Any<CancellationToken>())
            .Returns("true");

        var result = await _service.IsEmailPausedAsync();

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetEmailPausedAsync_WritesSetting()
    {
        await _service.SetEmailPausedAsync(true);

        await _systemSettings.Received(1).SetValueAsync(
            SystemSettingKeys.IsEmailSendingPaused,
            "true",
            Arg.Any<CancellationToken>());
    }

    private static EmailOutboxMessage BuildMessage(Instant createdAt, Guid? userId = null) => new()
    {
        Id = Guid.NewGuid(),
        RecipientEmail = "human@example.org",
        Subject = "Subject",
        HtmlBody = "<p>Body</p>",
        Status = EmailOutboxStatus.Queued,
        CreatedAt = createdAt,
        UserId = userId
    };
}
