using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Holded.Tests;

public sealed class HoldedWriteTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 30, 12, 0);
    private static readonly LocalDate Date = new(2026, 10, 1);

    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly Repository _repo;
    private readonly HoldedWrite _write;

    public HoldedWriteTests()
    {
        var options = new DbContextOptionsBuilder<HoldedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _repo = new Repository(new TestDbContextFactory<HoldedDbContext>(options));
        _write = new HoldedWrite(_client, _repo, _holded, NullLogger<HoldedWrite>.Instance);
    }

    [HumansFact]
    public async Task EnsureExpenseAccount_NumberMissingEverywhere_IsCreated()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync([new HoldedAccount { Number = 62900128, HoldedId = "a", Name = "Camp" }], Now, ct);
        // Made in Holded's UI since the last sync: in the live list, not in the mirror.
        _client.ListExpenseAccountsAsync(Arg.Any<CancellationToken>()).Returns(
            [new HoldedExpenseAccountDto { Id = "c", AccountNum = 62900130 }]);
        _client.CreateExpenseAccountAsync(62900129, "Art", Arg.Any<CancellationToken>()).Returns("b");

        (await _write.EnsureExpenseAccountAsync(62900128, "Camp", ct)).Should().Be(new HoldedExpenseAccountOutcome("a", false));
        (await _write.EnsureExpenseAccountAsync(62900130, "Sound", ct)).Should().Be(new HoldedExpenseAccountOutcome("c", false));
        (await _write.EnsureExpenseAccountAsync(62900129, "Art", ct)).Should().Be(new HoldedExpenseAccountOutcome("b", true));

        await _client.Received(1).CreateExpenseAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _holded.Received(2).SyncChartAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostJournalEntry_UnreadableSuccess_IsUnconfirmed()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.PostLedgerEntryAsync(Date, 57200001, 41000012, 50m, "x", Arg.Any<CancellationToken>())
            .Returns("unconfirmed:entry");

        (await _write.PostJournalEntryAsync(Date, 57200001, 41000012, 50m, "x", ct)).Confirmed.Should().BeFalse();
    }

    [HumansFact]
    public async Task IssueSalesDocument_UnknownAccountNumber_IsRefused()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync([new HoldedAccount { Number = 70500000, HoldedId = "rev", Name = "Ventas" }], Now, ct);
        var doc = new HoldedSalesDocumentDto { Id = "s1", DocNumber = "F1", Subtotal = 1, Tax = 0, Total = 1, Lines = [], RawJson = "{}" };
        _client.CreateSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, Arg.Any<HoldedSalesDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("s1");
        _client.GetSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, "s1", Arg.Any<CancellationToken>()).Returns(doc);
        HoldedSalesDocumentInput Input(int num) => new()
        {
            ContactName = "Camp",
            Date = Now,
            Description = "d",
            Notes = "n",
            Tags = [],
            Lines = [new HoldedSalesDocumentLineInput { Name = "Ice", Units = 1, Price = 1, AccountNum = num }],
        };

        (await _write.IssueSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, Input(70500000), ct)).Should().Be(doc);
        var act = () => _write.IssueSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, Input(70599999), ct);

        await act.Should().ThrowAsync<HoldedPermanentException>();
        await _client.Received(1).CreateSalesDocumentAsync(HoldedSalesDocumentKind.Invoice,
            Arg.Is<HoldedSalesDocumentInput>(i => i.Lines.Single().AccountId == "rev"), Arg.Any<CancellationToken>());
        await _client.Received(1).ApproveSalesDocumentAsync(HoldedSalesDocumentKind.Invoice, "s1", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsurePurchaseDocumentApproved_NotDraft_IsNotApproved()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.GetPurchaseDocumentAsync("d1", Arg.Any<CancellationToken>()).Returns(Doc(approvedAt: Now));
        _client.GetPurchaseDocumentAsync("d2", Arg.Any<CancellationToken>()).Returns(Doc(approvedAt: null));

        await _write.EnsurePurchaseDocumentApprovedAsync("d1", ct);
        await _write.EnsurePurchaseDocumentApprovedAsync("d2", ct);

        await _client.Received(1).ApprovePurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _client.Received(1).ApprovePurchaseDocumentAsync("d2", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateContact_Created_RefreshesContactMirror()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c9");

        (await _write.CreateContactAsync(new HoldedContactInput { Name = "Camp", Type = "client" }, ct)).Should().Be("c9");

        await _holded.Received(1).SyncContactsAsync(Arg.Any<CancellationToken>());
    }

    private static HoldedPurchaseDocumentDto Doc(Instant? approvedAt) => new()
    {
        ApprovedAt = approvedAt,
    };
}
