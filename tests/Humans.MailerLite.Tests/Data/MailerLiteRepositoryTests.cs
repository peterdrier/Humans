using AwesomeAssertions;
using Humans.MailerLite.Data;
using Humans.MailerLite.Domain;
using Humans.MailerLite.Tests.Infrastructure;
using NodaTime;

namespace Humans.MailerLite.Tests.Data;

public sealed class MailerLiteRepositoryTests
{
    private readonly IMailerLiteRepository _repo = InMemoryMailerLiteRepository.New();

    private static MailerLiteSyncState State(string key, Instant at, string summary = "") =>
        new() { Key = key, LastSyncAt = at, Summary = summary };

    [HumansFact]
    public async Task Upsert_ConcurrentWritesOnOneKey_ProduceOneRow()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var at = Instant.FromUtc(2026, 8, 21, 6, 0);

        // The daily job and an admin's "Sync Audience" click landing together.
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => _repo.UpsertSyncStateAsync(State("has-shift", at.Plus(Duration.FromSeconds(i))), ct)));

        var rows = await _repo.GetSyncStatesAsync(ct);
        rows.Should().ContainSingle().Which.Key.Should().Be("has-shift");
    }

    [HumansFact]
    public async Task Upsert_DifferentKeys_EachGetTheirOwnRow()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var at = Instant.FromUtc(2026, 8, 21, 6, 0);

        await _repo.UpsertSyncStateAsync(State("has-shift", at), ct);
        await _repo.UpsertSyncStateAsync(State(MailerLiteSyncKeys.Reconciliation, at), ct);

        (await _repo.GetSyncStatesAsync(ct)).Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Upsert_KeepsTheRowId_AndOverwritesTheValues()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var first = await _repo.UpsertSyncStateAsync(
            State("has-shift", Instant.FromUtc(2026, 8, 20, 6, 0), "first run"), ct);

        var second = await _repo.UpsertSyncStateAsync(
            State("has-shift", Instant.FromUtc(2026, 8, 21, 6, 0), "second run"), ct);

        second.Id.Should().Be(first.Id);
        var current = await _repo.GetSyncStateAsync("has-shift", ct);
        current!.Summary.Should().Be("second run");
        current.LastSyncAt.Should().Be(Instant.FromUtc(2026, 8, 21, 6, 0));
    }

    [HumansFact]
    public async Task GetSyncState_UnknownKey_IsNull()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        (await _repo.GetSyncStateAsync("never-synced", ct)).Should().BeNull();
    }
}
