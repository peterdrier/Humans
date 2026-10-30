using AwesomeAssertions;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Domain;
using Humans.Holded.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
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
        _write = new HoldedWrite(_client, _repo, _holded, new FakeClock(Now), NullLogger<HoldedWrite>.Instance);
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new HoldedContactDto { Id = "c1", Name = "Ana García", Type = "creditor", SupplierAccountNum = 41000012 },
        ]);
    }

    private void DocumentExists(string notes, decimal paid = 0m, bool approved = true)
    {
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new HoldedPurchaseDocListItemDto
            {
                Id = "d1", DocNumber = "C-1", ContactId = "c1", ContactName = "Ana García",
                Description = "Expense report 7", Date = Date.AtStartOfDayInZone(HoldedJson.MadridZone).ToInstant(),
                Subtotal = 50m, Tax = 0m, Total = 50m,
            },
        ]);
        _client.GetPurchaseDocumentAsync("d1", Arg.Any<CancellationToken>()).Returns(new HoldedPurchaseDocumentDto
        {
            Id = "d1", DocNumber = "C-1", Subtotal = 50m, Tax = 0m, Total = 50m,
            PaymentsTotal = paid, PaymentsPending = 50m - paid, Notes = notes,
            ApprovedAt = approved ? Now : null,
        });
    }

    [HumansFact]
    public async Task EnsureCreditorContact_reuses_a_creditor_by_name_and_creates_otherwise()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        (await _write.EnsureCreditorContactAsync("Ana García", null, null, "user-1", ct))
            .Should().Be(new HoldedCreditorContactOutcome(41000012, Created: false));

        _client.CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c2");
        _client.GetContactAsync("c2", Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "c2", SupplierAccountNum = 41000013 });
        (await _write.EnsureCreditorContactAsync("Bo Lind", "Bo", "ES00", "user-2", ct))
            .Should().Be(new HoldedCreditorContactOutcome(41000013, Created: true));
    }

    [HumansFact]
    public async Task EnsurePurchaseDocument_creates_on_the_mirrored_expense_account_attaches_and_approves()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync(
            [new HoldedAccount { Number = 62900128, HoldedId = "acc-629", Name = "Camp" }], Now, ct);
        _client.CreatePurchaseDocumentAsync(Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>())
            .Returns("d1");
        _client.GetPurchaseDocumentAsync("d1", Arg.Any<CancellationToken>()).Returns(new HoldedPurchaseDocumentDto
        {
            Id = "d1", DocNumber = "C-1", Subtotal = 50m, Tax = 0m, Total = 50m, PaymentsTotal = 0m, PaymentsPending = 50m,
        });
        var attachment = new HoldedAttachmentInput { FileName = "r.pdf", ContentType = "application/pdf", Content = Stream.Null };

        var outcome = await _write.EnsurePurchaseDocumentAsync(
            41000012, 62900128, "Expense report 7", 50m, Date, "humans:report:7", attachment, false, ct);

        outcome.Should().Be(new HoldedPurchaseDocumentOutcome(Created: true, Attached: true, Approved: true, "C-1"));
        await _client.Received(1).CreatePurchaseDocumentAsync(
            Arg.Is<HoldedPurchaseDocumentInput>(i => i.ContactId == "c1" && i.Lines.Single().AccountId == "acc-629"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsurePurchaseDocument_finds_the_existing_document_and_writes_nothing_more()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        DocumentExists("humans:report:7");

        var outcome = await _write.EnsurePurchaseDocumentAsync(
            41000012, 62900128, "Expense report 7", 50m, Date, "humans:report:7", null, true, ct);

        outcome.Should().Be(new HoldedPurchaseDocumentOutcome(false, false, false, "C-1"));
        await _client.DidNotReceive().CreatePurchaseDocumentAsync(
            Arg.Any<HoldedPurchaseDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureExpenseAccount_creates_only_a_number_missing_from_the_chart_and_refreshes_it()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync([new HoldedAccount { Number = 62900128, HoldedId = "a", Name = "Camp" }], Now, ct);
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new HoldedAccountDto { Id = "a", Number = 62900128, Name = "Camp", Debit = 0, Credit = 0, Balance = 0 },
            new HoldedAccountDto { Id = "b", Number = 62900129, Name = "Art", Debit = 0, Credit = 0, Balance = 0 },
        ]);

        (await _write.EnsureExpenseAccountAsync(62900128, "Camp", ct)).Should().BeFalse();
        (await _write.EnsureExpenseAccountAsync(62900129, "Art", ct)).Should().BeTrue();

        await _client.Received(1).CreateExpenseAccountAsync(62900129, "Art", Arg.Any<CancellationToken>());
        (await _repo.GetAccountsAsync(ct)).Select(a => a.Number).Should().Contain(62900129);
    }

    [HumansTheory]
    [Xunit.InlineData("humans:report:7", 0, HoldedDeleteOutcome.Deleted)]
    [Xunit.InlineData("someone else's", 0, HoldedDeleteOutcome.RefusedNotesDiffer)]
    [Xunit.InlineData("humans:report:7", 10, HoldedDeleteOutcome.RefusedPaid)]
    public async Task DeletePurchaseDocument_deletes_only_its_own_unpaid_document(
        string notes, decimal paid, HoldedDeleteOutcome expected)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        DocumentExists(notes, paid);

        (await _write.DeletePurchaseDocumentAsync(41000012, "Expense report 7", 50m, Date, "humans:report:7", ct))
            .Should().Be(expected);

        var deleted = expected == HoldedDeleteOutcome.Deleted ? 1 : 0;
        await _client.Received(deleted).DeletePurchaseDocumentAsync("d1", Arg.Any<CancellationToken>());
        await _holded.Received(deleted).SyncLedgerAsync(false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostJournalEntry_reports_an_unreadable_success_as_unconfirmed()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.PostLedgerEntryAsync(Date, 57200001, 41000012, 50m, "x", Arg.Any<CancellationToken>())
            .Returns("unconfirmed:entry");

        (await _write.PostJournalEntryAsync(Date, 57200001, 41000012, 50m, "x", ct)).Confirmed.Should().BeFalse();
    }
}
