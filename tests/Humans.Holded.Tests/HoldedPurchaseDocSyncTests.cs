using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Holded.Tests;

/// <summary>The purchase-document mirror: what a sync stores, how notes are fetched, and what a failed run leaves.</summary>
public sealed class HoldedPurchaseDocSyncTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 10, 30, 12, 0);
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly Service _service;

    public HoldedPurchaseDocSyncTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var callLog = Substitute.For<IHoldedCallLog>();
        callLog.DrainAll().Returns([]);
        _service = new Service(new Repository(new TestDbContextFactory<HoldedDbContext>(options)), _client, callLog,
            new FakeClock(FixedNow), Options.Create(new HoldedSectionOptions()), NullLogger<Service>.Instance);
        _client.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Full(ci.Arg<string>(), $"notes {ci.Arg<string>()}"));
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static HoldedPurchaseDocListItemDto Listed(string id, int day = 1, bool? draft = false) => new()
    {
        Id = id,
        DocNumber = id.ToUpperInvariant(),
        ContactName = "Vendor",
        Date = new LocalDate(2026, 4, day).AtStartOfDayInZone(Madrid).ToInstant(),
        Subtotal = 100m,
        Tax = 21m,
        Total = 121m,
        PaymentsPending = 121m,
        IsDraft = draft,
    };

    private static HoldedPurchaseDocumentDto Full(string id, string? notes) => new()
    {
        Id = id,
        DocNumber = id,
        Subtotal = 0m,
        Tax = 0m,
        Total = 0m,
        PaymentsTotal = 0m,
        PaymentsPending = 0m,
        Notes = notes,
    };

    private void StubList(params HoldedPurchaseDocListItemDto[] docs) =>
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(docs);

    [HumansFact]
    public async Task Sync_StoresTheDocsRaw_AndAnAbsentDraftFlagIsNotApproved()
    {
        StubList(
            Listed("a") with
            {
                Description = "  ER: Train  ",
                Tags = ["comms"],
                Lines = [new() { AccountId = "acc-1", Tags = ["ops"] }, new() { AccountId = "acc-2" }],
            },
            Listed("b", draft: null) with { Description = "   " });

        (await _service.SyncPurchaseDocumentsAsync(Ct)).Should().BeTrue();

        var mirror = await _service.GetPurchaseDocumentsAsync(Ct);
        mirror.LastSyncAt.Should().Be(FixedNow);
        mirror.LastCount.Should().Be(2);
        var a = mirror.Docs.Single(d => string.Equals(d.Id, "a", StringComparison.Ordinal));
        a.IsApproved.Should().BeTrue();
        a.Description.Should().Be("ER: Train");
        a.Tags.Should().Equal("comms", "ops");
        a.BookedAccountId.Should().Be("acc-1");
        a.PaymentsPending.Should().Be(121m);
        a.Date.Should().Be(new LocalDate(2026, 4, 1));
        a.HoldedUrl.Should().Be("https://app.holded.com/expenses/list#open:purchase-a");
        var b = mirror.Docs.Single(d => string.Equals(d.Id, "b", StringComparison.Ordinal));
        b.IsApproved.Should().BeFalse();
        b.Description.Should().BeNull();
        b.BookedAccountId.Should().BeNull();
    }

    [HumansFact]
    public async Task Sync_FetchesNotesOnce_AndAResyncKeepsThem_WhileADroppedDocLeaves()
    {
        StubList(Listed("a"), Listed("gone"));
        await _service.SyncPurchaseDocumentsAsync(Ct);
        StubList(Listed("a"));
        _client.ClearReceivedCalls();

        await _service.SyncPurchaseDocumentsAsync(Ct);

        await _client.DidNotReceive().GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var mirror = await _service.GetPurchaseDocumentsAsync(Ct);
        mirror.Docs.Should().ContainSingle().Which.Notes.Should().Be("notes a");
    }

    [HumansFact]
    public async Task Sync_CapsNotesFetchesPerRun_NewestFirst()
    {
        StubList(Enumerable.Range(1, 28).Select(day => Listed($"d{day}", day))
            .Concat(Enumerable.Range(1, 28).Select(day => Listed($"e{day}", day)))
            .ToArray());

        await _service.SyncPurchaseDocumentsAsync(Ct);

        await _client.Received(50).GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var unfetched = (await _service.GetPurchaseDocumentsAsync(Ct)).Docs.Where(d => d.Notes is null).ToList();
        unfetched.Should().HaveCount(6).And.OnlyContain(d => d.Date.Day <= 3);
    }

    [HumansFact]
    public async Task Sync_TransientNotesFailure_StopsFetching_AndTheSyncSucceeds()
    {
        StubList(Listed("a", 2), Listed("b", 1));
        _client.GetPurchaseDocumentAsync("a", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedTransientException("timeout"));

        (await _service.SyncPurchaseDocumentsAsync(Ct)).Should().BeTrue();

        await _client.Received(1).GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await _service.GetPurchaseDocumentsAsync(Ct)).Docs.Should().OnlyContain(d => d.Notes == null);
    }

    [HumansFact]
    public async Task Sync_FailedList_RecordsTheError_AndKeepsTheLastGoodMirror()
    {
        StubList(Listed("a"));
        await _service.SyncPurchaseDocumentsAsync(Ct);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedTransientException("holded down"));

        var sync = () => _service.SyncPurchaseDocumentsAsync(Ct);

        await sync.Should().ThrowAsync<HoldedTransientException>();
        var mirror = await _service.GetPurchaseDocumentsAsync(Ct);
        mirror.Status.Should().Be("Error");
        mirror.LastError.Should().Contain("holded down");
        mirror.LastSyncAt.Should().Be(FixedNow);
        mirror.Docs.Should().ContainSingle();
    }

    [HumansFact]
    public async Task ContactSync_ReplacesTheMirror_WhichIsNullUntilTheFirstSync()
    {
        (await _service.GetContactsAsync(Ct)).LastSyncAt.Should().BeNull();
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(
            [new HoldedContactDto { Id = "c1", Name = "Ana", SupplierAccountNum = 40000004, Iban = "ES79" }],
            [new HoldedContactDto { Id = "c2", Name = "Ben" }]);

        (await _service.SyncContactsAsync(Ct)).Should().BeTrue();
        (await _service.GetContactsAsync(Ct)).Contacts.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = "c1", Name = "Ana", SupplierAccountNum = 40000004, Iban = "ES79" });

        (await _service.SyncContactsAsync(Ct)).Should().BeTrue();
        (await _service.GetContactsAsync(Ct)).Contacts.Should().ContainSingle().Which.Id.Should().Be("c2");
    }
}
