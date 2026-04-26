using AwesomeAssertions;
using Humans.Application.DTOs.Finance;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

/// <summary>
/// End-to-end tests for <see cref="HoldedSyncService.SyncAsync"/>: state-machine
/// transitions (Idle ↔ Running ↔ Error), the no-op-when-already-running guard,
/// and the upsert pipeline that flows fetched docs through match resolution
/// and into <see cref="IHoldedRepository.UpsertManyAsync"/>.
/// </summary>
public class HoldedSyncServiceUpsertTests
{
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedRepository _repository = Substitute.For<IHoldedRepository>();
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 26, 12, 0));

    private HoldedSyncService CreateSut() =>
        new(_client, _repository, _budget, _audit, _clock, NullLogger<HoldedSyncService>.Instance);

    private static BudgetYear MakeYear() => new()
    {
        Id = Guid.NewGuid(),
        Year = "2026",
        Name = "FY 2026",
        Status = BudgetYearStatus.Active,
    };

    [HumansFact]
    public async Task SyncAsync_OnSuccess_MarksRunningThenIdleAndUpsertsAll()
    {
        _repository.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedSyncState { SyncStatus = HoldedSyncStatus.Idle });

        var year = MakeYear();
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(year);
        _budget.GetCategoryBySlugAsync(year.Id, "departments", "sound", Arg.Any<CancellationToken>())
            .Returns(new BudgetCategory { Id = Guid.NewGuid(), Name = "Sound", Slug = "sound" });

        var docs = new List<(HoldedDocDto, string)>
        {
            (new HoldedDocDto
            {
                Id = "h1", DocNumber = "F-1", Currency = "eur",
                Date = Instant.FromUtc(2026, 6, 1, 0, 0).ToUnixTimeSeconds(),
                Tags = new List<string> { "departments-sound" },
            }, """{"id":"h1"}"""),
            (new HoldedDocDto
            {
                Id = "h2", DocNumber = "F-2", Currency = "usd",
                Date = Instant.FromUtc(2026, 6, 2, 0, 0).ToUnixTimeSeconds(),
                Tags = new List<string>(),
            }, """{"id":"h2"}"""),
        };
        _client.GetAllPurchaseDocsAsync(Arg.Any<CancellationToken>()).Returns(docs);

        IReadOnlyList<HoldedTransaction>? captured = null;
        await _repository.UpsertManyAsync(
            Arg.Do<IReadOnlyList<HoldedTransaction>>(t => captured = t),
            Arg.Any<CancellationToken>());

        var sut = CreateSut();
        var result = await sut.SyncAsync();

        result.DocsFetched.Should().Be(2);
        result.Matched.Should().Be(1);
        result.Unmatched.Should().Be(1);
        result.ByStatus.Should().ContainKey(nameof(HoldedMatchStatus.Matched));
        result.ByStatus.Should().ContainKey(nameof(HoldedMatchStatus.UnsupportedCurrency));

        captured.Should().NotBeNull();
        captured!.Should().HaveCount(2);
        captured.Single(t => string.Equals(t.HoldedDocId, "h1", StringComparison.Ordinal))
            .MatchStatus.Should().Be(HoldedMatchStatus.Matched);
        captured.Single(t => string.Equals(t.HoldedDocId, "h2", StringComparison.Ordinal))
            .MatchStatus.Should().Be(HoldedMatchStatus.UnsupportedCurrency);

        await _repository.Received(1).SetSyncStateAsync(
            HoldedSyncStatus.Running, Arg.Any<Instant>(), null, Arg.Any<CancellationToken>());
        await _client.Received(1).GetAllPurchaseDocsAsync(Arg.Any<CancellationToken>());
        await _repository.Received(1).UpsertManyAsync(
            Arg.Any<IReadOnlyList<HoldedTransaction>>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).RecordSyncCompletedAsync(
            Arg.Any<Instant>(), 2, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_WhenAlreadyRunning_SkipsAndDoesNotCallClient()
    {
        _repository.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedSyncState { SyncStatus = HoldedSyncStatus.Running });

        var sut = CreateSut();
        var result = await sut.SyncAsync();

        result.DocsFetched.Should().Be(0);
        result.Matched.Should().Be(0);
        result.Unmatched.Should().Be(0);
        await _client.DidNotReceive().GetAllPurchaseDocsAsync(Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().SetSyncStateAsync(
            Arg.Any<HoldedSyncStatus>(), Arg.Any<Instant>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncAsync_OnException_MarksErrorWithLastError()
    {
        _repository.GetSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedSyncState { SyncStatus = HoldedSyncStatus.Idle });
        _client.GetAllPurchaseDocsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<(HoldedDocDto, string)>>>(_ => throw new InvalidOperationException("boom"));

        var sut = CreateSut();

        var act = async () => await sut.SyncAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        await _repository.Received(1).SetSyncStateAsync(
            HoldedSyncStatus.Running, Arg.Any<Instant>(), null, Arg.Any<CancellationToken>());
        await _repository.Received(1).SetSyncStateAsync(
            HoldedSyncStatus.Error, Arg.Any<Instant>(), "boom", Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().RecordSyncCompletedAsync(
            Arg.Any<Instant>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
