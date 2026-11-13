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
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new HoldedContactDto { Id = "c1", Name = "Ana García", SupplierAccountNum = 41000012 },
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
            Id = "d1",
            DocNumber = "C-1",
            Subtotal = 50m,
            Tax = 0m,
            Total = 50m,
            PaymentsTotal = paid,
            PaymentsPending = 50m - paid,
            Notes = notes,
            ApprovedAt = approved ? Now : null,
        });
    }

    [HumansFact]
    public async Task EnsureCreditorContact_reuses_a_creditor_range_contact_by_name_and_creates_otherwise()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        (await _write.EnsureCreditorContactAsync("Ana García", null, null, ct))
            .Should().Be(new HoldedCreditorContactOutcome(41000012, Created: false));

        _client.CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c2");
        _client.GetContactAsync("c2", Arg.Any<CancellationToken>())
            .Returns(new HoldedContactDto { Id = "c2", SupplierAccountNum = 41000013 });
        (await _write.EnsureCreditorContactAsync("Bo Lind", "Bo", "ES00", ct))
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
            Id = "d1",
            DocNumber = "C-1",
            Subtotal = 50m,
            Tax = 0m,
            Total = 50m,
            PaymentsTotal = 0m,
            PaymentsPending = 50m,
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
    public async Task EnsureExpenseAccount_creates_only_a_number_missing_from_the_mirror_and_from_Holded()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await _repo.UpsertAccountsAsync([new HoldedAccount { Number = 62900128, HoldedId = "a", Name = "Camp" }], Now, ct);
        // Made in Holded's UI since the last sync: in the live list, not in the mirror.
        _client.ListExpenseAccountsAsync(Arg.Any<CancellationToken>()).Returns(
            [new HoldedExpenseAccountDto { Id = "c", AccountNum = 62900130, Name = "Sound" }]);
        _client.CreateExpenseAccountAsync(62900129, "Art", Arg.Any<CancellationToken>()).Returns("b");

        (await _write.EnsureExpenseAccountAsync(62900128, "Camp", ct)).Should().Be(new HoldedExpenseAccountOutcome("a", false));
        (await _write.EnsureExpenseAccountAsync(62900130, "Sound", ct)).Should().Be(new HoldedExpenseAccountOutcome("c", false));
        (await _write.EnsureExpenseAccountAsync(62900129, "Art", ct)).Should().Be(new HoldedExpenseAccountOutcome("b", true));

        await _client.Received(1).CreateExpenseAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _holded.Received(2).SyncChartAsync(Arg.Any<CancellationToken>());
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

    [HumansFact]
    public async Task IssueSalesDocument_books_each_line_to_the_chart_id_of_its_number_and_refuses_an_unknown_one()
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
    public async Task EnsurePurchaseDocumentApproved_approves_only_a_draft()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.GetPurchaseDocumentAsync("d1", Arg.Any<CancellationToken>()).Returns(Doc("d1", approvedAt: Now));
        _client.GetPurchaseDocumentAsync("d2", Arg.Any<CancellationToken>()).Returns(Doc("d2", approvedAt: null));

        await _write.EnsurePurchaseDocumentApprovedAsync("d1", ct);
        await _write.EnsurePurchaseDocumentApprovedAsync("d2", ct);

        await _client.Received(1).ApprovePurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _client.Received(1).ApprovePurchaseDocumentAsync("d2", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateContact_refreshes_the_contact_mirror()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        _client.CreateContactAsync(Arg.Any<HoldedContactInput>(), Arg.Any<CancellationToken>()).Returns("c9");

        (await _write.CreateContactAsync(new HoldedContactInput { Name = "Camp", Type = "client" }, ct)).Should().Be("c9");

        await _holded.Received(1).SyncContactsAsync(Arg.Any<CancellationToken>());
    }

    private static HoldedPurchaseDocumentDto Doc(string id, Instant? approvedAt) => new()
    {
        Id = id,
        DocNumber = id,
        Subtotal = 0,
        Tax = 0,
        Total = 0,
        PaymentsTotal = 0,
        PaymentsPending = 0,
        ApprovedAt = approvedAt,
    };
}
