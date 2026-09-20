using AwesomeAssertions;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Email.Services;
using Humans.Base.Configuration;
using Humans.Settings.Contracts;
using Microsoft.Extensions.Options;
using Humans.Base.Enums;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Email.Tests.Services;

public sealed class EmailOutboxServiceTests
{
    private readonly Instant _now = Instant.FromUtc(2026, 5, 10, 12, 0);
    private readonly IEmailOutboxRepository _repo = Substitute.For<IEmailOutboxRepository>();
    private readonly ISettingsService _settingsStore = Substitute.For<ISettingsService>();
    private readonly EmailOutboxService _service;

    public EmailOutboxServiceTests()
    {
        _service = new EmailOutboxService(
            _repo, _settingsStore, Options.Create(new EmailSettings()), new FakeClock(_now));
    }

    [HumansFact]
    public async Task GetOutboxStatsAsync_ReturnsNewestRecentMessages()
    {
        var middle = BuildMessage(_now - Duration.FromHours(1));
        var newer = BuildMessage(_now);

        _repo.GetRecentAsync(2, Arg.Any<CancellationToken>())
            .Returns([newer, middle]);

        var result = await _service.GetOutboxStatsAsync(recentMessageCount: 2, cancellationToken: Xunit.TestContext.Current.CancellationToken);

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

        var result = await _service.GetMessagesForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Select(m => m.Id).Should().Equal(newer.Id, older.Id);
    }

    [HumansFact]
    public async Task IsEmailPausedAsync_ReturnsTrueWhenSettingIsTrue()
    {
        _settingsStore.GetValueAsync(
                SettingKeys.IsEmailSendingPaused,
                Arg.Any<CancellationToken>())
            .Returns("true");

        var result = await _service.IsEmailPausedAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetEmailPausedAsync_WritesSetting()
    {
        await _service.SetEmailPausedAsync(true, Xunit.TestContext.Current.CancellationToken);

        await _settingsStore.Received(1).SetValueAsync(
            SettingKeys.IsEmailSendingPaused,
            "true",
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ContributeForUserAsync_EmitsTheOutboxSliceForTheHuman()
    {
        var userId = Guid.NewGuid();
        _repo.GetForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([BuildMessage(_now, userId)]);

        var slices = await _service.ContributeForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        var slice = slices.Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(EmailOutboxService.EmailOutbox);
        System.Text.Json.JsonSerializer.Serialize(slice.Data)
            .Should().Contain("human@example.org");
    }

    [HumansFact]
    public async Task EraseForUserAsync_DropsEveryRowForTheHuman()
    {
        // Not just the Sent ones the retention sweep reaches — failed and queued
        // rows carry the same address, name and rendered body.
        var userId = Guid.NewGuid();

        await _service.EraseForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).DeleteForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Daily send counts (#1195)
    // ==========================================================================

    [HumansFact]
    public async Task GetDailySendCountsAsync_AggregatesAcrossTemplatesPerDay()
    {
        var day = new LocalDate(2026, 8, 15);
        _repo.GetDailySendCountsSinceAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new EmailDailySendCount { Date = day, TemplateName = "welcome", SentCount = 3, FailedCount = 1 },
                new EmailDailySendCount { Date = day, TemplateName = "reminder", SentCount = 2, FailedCount = 0 },
            ]);

        var result = await _service.GetDailySendCountsAsync(90, Xunit.TestContext.Current.CancellationToken);

        var byDay = result.ByDay.Should().ContainSingle().Subject;
        byDay.Date.Should().Be(day);
        byDay.SentCount.Should().Be(5);
        byDay.FailedCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetDailySendCountsAsync_RanksTopTemplatesByVolume()
    {
        var day = new LocalDate(2026, 8, 15);
        _repo.GetDailySendCountsSinceAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new EmailDailySendCount { Date = day, TemplateName = "small", SentCount = 1, FailedCount = 0 },
                new EmailDailySendCount { Date = day, TemplateName = "big", SentCount = 100, FailedCount = 0 },
            ]);

        var result = await _service.GetDailySendCountsAsync(90, Xunit.TestContext.Current.CancellationToken);

        result.TopTemplates.First().TemplateName.Should().Be("big");
    }

    [HumansFact]
    public async Task PreviewDailySendCountBackfillAsync_ExcludesExistingKeys()
    {
        var day = new LocalDate(2026, 8, 15);
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([BuildOutboxMessage(day.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Sent, "welcome")]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)> { (day, "welcome") });

        var preview = await _service.PreviewDailySendCountBackfillAsync(Xunit.TestContext.Current.CancellationToken);

        preview.RowsToAdd.Should().Be(0);
    }

    [HumansFact]
    public async Task PreviewDailySendCountBackfillAsync_IncludesMissingKeys()
    {
        var day = new LocalDate(2026, 8, 15);
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([BuildOutboxMessage(day.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Sent, "welcome")]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)>());

        var preview = await _service.PreviewDailySendCountBackfillAsync(Xunit.TestContext.Current.CancellationToken);

        preview.RowsToAdd.Should().Be(1);
        preview.Sample.Should().ContainSingle(r => r.Date == day && r.TemplateName == "welcome" && r.SentCount == 1);
    }

    [HumansFact]
    public async Task PreviewDailySendCountBackfillAsync_ExcludesTestAddresses()
    {
        var day = new LocalDate(2026, 8, 15);
        var testMessage = BuildOutboxMessage(day.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Sent, "welcome");
        testMessage.RecipientEmail = "bot@localhost";
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([testMessage]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)>());

        var preview = await _service.PreviewDailySendCountBackfillAsync(Xunit.TestContext.Current.CancellationToken);

        preview.RowsToAdd.Should().Be(0);
    }

    [HumansFact]
    public async Task PreviewDailySendCountBackfillAsync_UsesSentAtDateForSentRows()
    {
        // Sent on 8/16 despite being created on 8/15 — the sent-day is authoritative for Sent rows.
        var createdAt = new LocalDate(2026, 8, 15).AtMidnight().InUtc().ToInstant();
        var sentAt = new LocalDate(2026, 8, 16).AtMidnight().InUtc().ToInstant();
        var sent = BuildOutboxMessage(createdAt, EmailOutboxStatus.Sent, "welcome");
        sent.SentAt = sentAt;

        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([sent]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)>());

        var preview = await _service.PreviewDailySendCountBackfillAsync(Xunit.TestContext.Current.CancellationToken);

        preview.Sample.Should().ContainSingle(r => r.Date == new LocalDate(2026, 8, 16) && r.TemplateName == "welcome");
    }

    [HumansFact]
    public async Task PreviewDailySendCountBackfillAsync_NeverBackfillsFailedCount()
    {
        // Per issue #1195: a failed message's actual failure day isn't recoverable
        // from the outbox's final-state-only rows, so failures are never
        // backfilled — FailedCount stays 0 and no row is produced for a
        // template that only ever failed.
        var day = new LocalDate(2026, 8, 15);
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([BuildOutboxMessage(day.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Failed, "reminder")]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)>());

        var preview = await _service.PreviewDailySendCountBackfillAsync(Xunit.TestContext.Current.CancellationToken);

        preview.RowsToAdd.Should().Be(0);
    }

    [HumansFact]
    public async Task PreviewDailySendCountBackfillAsync_ExcludesToday()
    {
        // The live processor may already be counting today — never merge a
        // historical group into a day it's still writing to.
        var today = _now.InUtc().Date;
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([BuildOutboxMessage(today.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Sent, "welcome")]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)>());

        var preview = await _service.PreviewDailySendCountBackfillAsync(Xunit.TestContext.Current.CancellationToken);

        preview.RowsToAdd.Should().Be(0);
    }

    [HumansFact]
    public async Task BackfillDailySendCountsAsync_NeverOverwritesExistingKeysAndIsIdempotent()
    {
        var day = new LocalDate(2026, 8, 15);
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([BuildOutboxMessage(day.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Sent, "welcome")]);
        // Simulate the row already existing (written earlier by the processor or a prior backfill run).
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)> { (day, "welcome") });

        var added = await _service.BackfillDailySendCountsAsync(Xunit.TestContext.Current.CancellationToken);

        added.Should().Be(0);
        await _repo.DidNotReceive().AddDailySendCountsAsync(
            Arg.Any<IReadOnlyList<EmailDailySendCount>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillDailySendCountsAsync_AddsOnlyMissingRows()
    {
        var day = new LocalDate(2026, 8, 15);
        _repo.GetSentOrFailedSinceAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([BuildOutboxMessage(day.AtMidnight().InUtc().ToInstant(), EmailOutboxStatus.Sent, "welcome")]);
        _repo.GetDailySendCountKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<(LocalDate, string)>());

        var added = await _service.BackfillDailySendCountsAsync(Xunit.TestContext.Current.CancellationToken);

        added.Should().Be(1);
        await _repo.Received(1).AddDailySendCountsAsync(
            Arg.Is<IReadOnlyList<EmailDailySendCount>>(rows =>
                rows.Count == 1 && rows[0].Date == day && rows[0].TemplateName == "welcome" && rows[0].SentCount == 1),
            Arg.Any<CancellationToken>());
    }

    private static EmailOutboxMessage BuildOutboxMessage(Instant createdAt, EmailOutboxStatus status, string templateName) => new()
    {
        Id = Guid.NewGuid(),
        RecipientEmail = "human@example.org",
        Subject = "Subject",
        HtmlBody = "<p>Body</p>",
        TemplateName = templateName,
        Status = status,
        CreatedAt = createdAt,
        SentAt = status == EmailOutboxStatus.Sent ? createdAt : null,
    };

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
