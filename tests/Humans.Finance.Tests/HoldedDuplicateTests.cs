using Humans.Base.Caching;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// The duplicate purchase documents the 2026-10-18 outbox race left in Holded: the matcher that
/// finds them, the dry run on /Finance/HoldedDuplicates, the Apply that deletes them, and the
/// nightly log line in the document sync. One rule, run from every entry point.
/// </summary>
public class HoldedDuplicateTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 10, 18, 14, 0);
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ReportId = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private const int Account = 41000067;

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly IExpenseHoldedBookingService _expenses = Substitute.For<IExpenseHoldedBookingService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    private Service MakeService(ILogger<Service>? logger = null) => new(
        _repo, _client, Substitute.For<IBudgetService>(), _holded, _expenses, new FakeClock(FixedNow),
        new MemoryCache(new MemoryCacheOptions()), _audit, Substitute.For<IUserService>(),
        Substitute.For<IUserEmailService>(), Substitute.For<IEmailService>(), TestFinanceEmails.Create(),
        Options.Create(new SepaOptions()),
        new TrackedCache<string, Dictionary<string, int>>("Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        logger ?? NullLogger<Service>.Instance);

    public HoldedDuplicateTests()
    {
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account)]);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns([Binding("c1")]);
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>());
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedAccountDto>());
        _holded.SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);
        // The single GET is the only read that carries notes; by default every document is the push's.
        _client.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => FullDoc(ci.Arg<string>()));
    }

    /// <summary>The single-GET shape of a document: the one read that returns <c>notes</c>.</summary>
    private static HoldedPurchaseDocumentDto FullDoc(string id, string? notes = null, bool noNotes = false) => new()
    {
        Id = id,
        DocNumber = id,
        Subtotal = 120m,
        Tax = 0m,
        Total = 120m,
        PaymentsTotal = 0m,
        PaymentsPending = 120m,
        Notes = noNotes ? null : notes ?? $"Expense report {ReportId}",
    };

    private static HoldedContactDto Contact(string id, int account, string name = "Catherine Morgaine Ryan") =>
        new() { Id = id, Name = name, SupplierAccountNum = account };

    private static HoldedCreditorContact Binding(string contactId, Guid? userId = null) => new()
    {
        UserId = userId ?? UserId,
        HoldedContactId = contactId,
        SupplierAccountNum = Account,
        Source = CreditorContactSource.Auto,
    };

    private static ExpenseHoldedBooking Booking(
        string docId, decimal amount = 120m, string description = "ER: Train to Madrid", Guid? user = null, string contactId = "c1",
        Guid? report = null) =>
        new(report ?? ReportId, Guid.NewGuid(), user ?? UserId, contactId, docId, description, $"Expense report {report ?? ReportId}", amount);

    /// <summary>A list item, as the v2 purchase list returns it: never any notes.</summary>
    private static HoldedPurchaseDocListItemDto Doc(
        string id, decimal total = 120m, string description = "ER: Train to Madrid", string contactId = "c1",
        decimal? pending = null, bool? draft = false) => new()
        {
            Id = id,
            DocNumber = id,
            ContactId = contactId,
            ContactName = "Catherine Morgaine Ryan",
            Description = description,
            Notes = null,
            Date = FixedNow,
            Subtotal = total,
            Tax = 0m,
            Total = total,
            PaymentsPending = pending ?? total,
            IsDraft = draft,
        };

    private static readonly IReadOnlyDictionary<Guid, IReadOnlySet<string>> NoExtraContacts =
        new Dictionary<Guid, IReadOnlySet<string>>();

    // ---- the matcher ------------------------------------------------------------------------------

    [HumansFact]
    public void Matcher_OneLineTwoDocuments_FlagsTheOneTheLineDoesNotLinkTo()
    {
        var matches = HoldedDuplicateMatcher.Find([Booking("doc-a")], [Doc("doc-a"), Doc("doc-b")], NoExtraContacts);

        var match = matches.Should().ContainSingle().Subject;
        match.Kept.Id.Should().Be("doc-a");
        match.Duplicates.Select(d => d.Id).Should().Equal("doc-b");
    }

    [HumansFact]
    public void Matcher_TwoIdenticalLinesTwoDocuments_FlagsNothing()
    {
        // Martina Mulas: two 3.40 tolls, two documents, each the kept one of its own line.
        var matches = HoldedDuplicateMatcher.Find(
            [Booking("doc-a", 3.40m, "ER: Toll"), Booking("doc-b", 3.40m, "ER: Toll")],
            [Doc("doc-a", 3.40m, "ER: Toll"), Doc("doc-b", 3.40m, "ER: Toll")], NoExtraContacts);

        matches.Should().BeEmpty();
    }

    [HumansFact]
    public void Matcher_TwoIdenticalLinesOneExtraDocument_ClaimsItOnce()
    {
        // The extra document matches both lines; it is one duplicate, counted and deleted once.
        var matches = HoldedDuplicateMatcher.Find(
            [Booking("doc-a", 10m, "ER: Taxi"), Booking("doc-b", 10m, "ER: Taxi")],
            [Doc("doc-a", 10m, "ER: Taxi"), Doc("doc-b", 10m, "ER: Taxi"), Doc("doc-c", 10m, "ER: Taxi")],
            NoExtraContacts);

        matches.SelectMany(m => m.Duplicates).Select(d => d.Id).Should().Equal("doc-c");
    }

    [HumansFact]
    public void Matcher_DifferentDescriptionTotalOrContact_IsNotADuplicate()
    {
        var matches = HoldedDuplicateMatcher.Find([Booking("doc-a")],
        [
            Doc("doc-a"),
            Doc("doc-desc", description: "ER: Bus to Madrid"),
            Doc("doc-total", total: 121m),
            Doc("doc-other-contact", contactId: "c-someone-else"),
        ], NoExtraContacts);

        matches.Should().BeEmpty();
    }

    [HumansFact]
    public void Matcher_ListItemsCarryNoNotes_SoNotesAreNotPartOfTheMatch()
    {
        // The v2 purchase list never returns `notes` (verified live 2026-10-21); requiring them
        // here is why the page reported zero while Holded held ~430 duplicates.
        var matches = HoldedDuplicateMatcher.Find([Booking("doc-a")], [Doc("doc-a"), Doc("doc-b")], NoExtraContacts);

        matches.Should().ContainSingle().Which.Duplicates.Select(d => d.Id).Should().Equal("doc-b");
    }

    [HumansFact]
    public void Matcher_SearchesEveryContactTheMemberHolds()
    {
        var contacts = new Dictionary<Guid, IReadOnlySet<string>>
        {
            [UserId] = new HashSet<string>(StringComparer.Ordinal) { "c1", "c2" },
        };

        var matches = HoldedDuplicateMatcher.Find([Booking("doc-a")], [Doc("doc-a"), Doc("doc-b", contactId: "c2")], contacts);

        matches.Should().ContainSingle().Which.Duplicates.Select(d => d.Id).Should().Equal("doc-b");
    }

    [HumansFact]
    public void Matcher_ALineWhoseKeptDocumentIsGone_IsSkipped()
    {
        var matches = HoldedDuplicateMatcher.Find([Booking("doc-missing")], [Doc("doc-b")], NoExtraContacts);

        matches.Should().BeEmpty();
    }

    // ---- the dry run --------------------------------------------------------------------------------

    [HumansFact]
    public async Task DryRun_ListsTheDuplicatePerMember_WithTotals()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a"), Doc("doc-b")]);

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        vm.Error.Should().BeNull();
        vm.HasWork.Should().BeTrue();
        vm.DocumentCount.Should().Be(1);
        vm.Total.Should().Be(120m);
        vm.ContactCount.Should().Be(0);
        var member = vm.Members.Should().ContainSingle().Subject;
        member.UserId.Should().Be(UserId);
        member.SupplierAccountNum.Should().Be(Account);
        member.BoundContactId.Should().Be("c1");
        var line = member.Lines.Should().ContainSingle().Subject;
        line.KeptDocId.Should().Be("doc-a");
        line.DeletableDocIds.Should().Equal("doc-b");
        line.Refused.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DryRun_ADuplicateWithAPayment_IsRefusedAndListedForAHuman()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns([Doc("doc-a"), Doc("doc-paid", pending: 0m), Doc("doc-draft", draft: true, pending: 0m)]);

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        var line = vm.Members.Single().Lines.Single();
        line.DeletableDocIds.Should().BeEmpty();
        line.Refused.Select(r => r.DocId).Should().BeEquivalentTo(["doc-paid", "doc-draft"]);
        line.Refused.Single(r => string.Equals(r.DocId, "doc-paid", StringComparison.Ordinal)).Reason.Should().Contain("payment");
        line.Refused.Single(r => string.Equals(r.DocId, "doc-draft", StringComparison.Ordinal)).Reason.Should().Contain("draft");
        vm.RefusedCount.Should().Be(2);
        vm.DocumentCount.Should().Be(0);
        vm.HasWork.Should().BeFalse();
    }

    [HumansFact]
    public async Task DryRun_ACandidateWhoseNotesAreNotThePushs_IsRefusedNotDeletable()
    {
        // Description and total match from the list; the single GET says the notes differ (another
        // report's push) or are empty (a hand-made document). Neither is this line's duplicate.
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns([Doc("doc-a"), Doc("doc-b"), Doc("doc-other"), Doc("doc-blank")]);
        _client.GetPurchaseDocumentAsync("doc-other", Arg.Any<CancellationToken>())
            .Returns(FullDoc("doc-other", $"Expense report {Guid.NewGuid()}"));
        _client.GetPurchaseDocumentAsync("doc-blank", Arg.Any<CancellationToken>())
            .Returns(FullDoc("doc-blank", noNotes: true));

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        var line = vm.Members.Single().Lines.Single();
        line.DeletableDocIds.Should().Equal("doc-b");
        line.Refused.Select(r => r.DocId).Should().BeEquivalentTo(["doc-other", "doc-blank"]);
        line.Refused.Should().AllSatisfy(r => r.Reason.Should().Contain("notes do not match the push"));
        await _client.DidNotReceive().GetPurchaseDocumentAsync("doc-a", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DryRun_TwoReportsWithIdenticalLines_TheDuplicateGoesToTheLineWhoseNotesItCarries()
    {
        // Repeated commute lines across reports: report 1's line is first in the list and would claim
        // doc-b2 on description + total alone, but doc-b2 carries report 2's notes, so it is report
        // 2's duplicate — deletable there, not refused under report 1.
        var report2 = Guid.NewGuid();
        var line2 = Booking("doc-b", report: report2);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a"), line2]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a"), Doc("doc-b"), Doc("doc-b2")]);
        _client.GetPurchaseDocumentAsync("doc-b2", Arg.Any<CancellationToken>())
            .Returns(FullDoc("doc-b2", $"Expense report {report2}"));

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        var line = vm.Members.Single().Lines.Should().ContainSingle().Subject;
        line.LineId.Should().Be(line2.LineId);
        line.KeptDocId.Should().Be("doc-b");
        line.DeletableDocIds.Should().Equal("doc-b2");
        line.Refused.Should().BeEmpty();
        vm.RefusedCount.Should().Be(0);
    }

    [HumansFact]
    public async Task DryRun_APaidOrDraftCandidate_IsRefusedWithoutTheGet()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns([Doc("doc-a"), Doc("doc-paid", pending: 0m), Doc("doc-draft", draft: true, pending: 0m)]);

        await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        await _client.DidNotReceive().GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DryRun_ContactPair_TheUnboundTwinIsAnOrphanOnceItsDocumentsAreAllDuplicates()
    {
        // Catherine Morgaine Ryan: bound to 41000067 (c1); the race made c2 on 41000068 with the same
        // name, carrying only the duplicate documents.
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account), Contact("c2", 41000068)]);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a"), Doc("doc-b", contactId: "c2")]);

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        var member = vm.Members.Should().ContainSingle().Subject;
        member.Lines.Single().DeletableDocIds.Should().Equal("doc-b");
        member.OrphanContactIds.Should().Equal("c2");
        vm.ContactCount.Should().Be(1);
    }

    [HumansFact]
    public async Task DryRun_AnUnboundTwinBoundToAnotherMember_IsNeverSearched()
    {
        var other = Guid.NewGuid();
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account), Contact("c2", 41000068)]);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns([Binding("c1"), Binding("c2", other)]);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a"), Doc("doc-b", contactId: "c2")]);

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        vm.Members.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DryRun_AnEmptyUnboundCreditorContact_IsListedNotTouched()
    {
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account), Contact("c-empty", 41000062, "Someone Else")]);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns(new List<ExpenseHoldedBooking>());
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedPurchaseDocListItemDto>());

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        vm.EmptyUnboundContactIds.Should().Equal("41000062 Someone Else (c-empty)");
        vm.HasWork.Should().BeFalse();
    }

    [HumansFact]
    public async Task DryRun_WhenHoldedCannotBeRead_SaysSoInsteadOfJudging()
    {
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Throws(new HoldedTransientException("timeout"));

        var vm = await MakeService().GetHoldedDuplicatesAsync(TestContext.Current.CancellationToken);

        vm.Error.Should().Contain("timeout");
        vm.Members.Should().BeEmpty();
    }

    // ---- apply --------------------------------------------------------------------------------------

    [HumansFact]
    public async Task Apply_DeletesTheDocumentsThenTheOrphanContact_AuditsEachId_AndResyncs()
    {
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account), Contact("c2", 41000068)]);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns([Doc("doc-a"), Doc("doc-b", contactId: "c2"), Doc("doc-c", contactId: "c2")]);
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(
            [Account_(Account, -360m)],
            [Account_(Account, -120m)]);

        var result = await MakeService().RemoveHoldedDuplicatesAsync(Actor);

        result.Error.Should().BeNull();
        result.SyncError.Should().BeNull();
        result.DeletedDocuments.Should().Be(2);
        result.DeletedContacts.Should().Be(1);
        result.Failures.Should().Be(0);
        var member = result.Members.Should().ContainSingle().Subject;
        member.DeletedDocIds.Should().Equal("doc-b", "doc-c");
        member.DeletedContactIds.Should().Equal("c2");
        member.BalanceBefore.Should().Be(-360m);
        member.BalanceAfter.Should().Be(-120m);

        Received.InOrder(() =>
        {
            _ = _client.DeletePurchaseDocumentAsync("doc-b", Arg.Any<CancellationToken>());
            _ = _client.DeletePurchaseDocumentAsync("doc-c", Arg.Any<CancellationToken>());
            _ = _client.DeleteContactAsync("c2", Arg.Any<CancellationToken>());
            _ = _holded.SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        });
        await _client.DidNotReceive().DeletePurchaseDocumentAsync("doc-a", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().DeleteContactAsync("c1", Arg.Any<CancellationToken>());

        await _audit.Received(1).LogAsync(AuditAction.HoldedDuplicateRemoved, "ExpenseLine", Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("doc-b") && d.Contains("kept doc-a") && d.Contains("41000067")),
            Actor, UserId, nameof(User));
        await _audit.Received(1).LogAsync(AuditAction.HoldedDuplicateRemoved, "ExpenseLine", Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("doc-c")), Actor, UserId, nameof(User));
        await _audit.Received(1).LogAsync(AuditAction.HoldedDuplicateRemoved, nameof(User), UserId,
            Arg.Is<string>(d => d.Contains("contact c2") && d.Contains("kept c1")), Actor);
    }

    [HumansFact]
    public async Task Apply_ARefusedDocumentKeepsItsContact_AndAFailedDeleteIsReported()
    {
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account), Contact("c2", 41000068)]);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns([Doc("doc-a"), Doc("doc-b", contactId: "c2"), Doc("doc-paid", contactId: "c2", pending: 0m)]);
        _client.DeletePurchaseDocumentAsync("doc-b", Arg.Any<CancellationToken>()).Throws(new HoldedPermanentException("409 in use"));

        var result = await MakeService().RemoveHoldedDuplicatesAsync(Actor);

        var member = result.Members.Should().ContainSingle().Subject;
        member.DeletedDocIds.Should().BeEmpty();
        member.Failures.Should().ContainSingle().Which.Should().Contain("doc-b");
        await _client.DidNotReceive().DeletePurchaseDocumentAsync("doc-paid", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().DeleteContactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().LogAsync(Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Apply_VerifiesNotesBySingleGetAtRunTime_AndDeletesOnlyVerifiedOnes()
    {
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns([Contact("c1", Account), Contact("c2", 41000068)]);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns([Doc("doc-a"), Doc("doc-b", contactId: "c2"), Doc("doc-other", contactId: "c2")]);
        _client.GetPurchaseDocumentAsync("doc-other", Arg.Any<CancellationToken>())
            .Returns(FullDoc("doc-other", $"Expense report {Guid.NewGuid()}"));

        var result = await MakeService().RemoveHoldedDuplicatesAsync(Actor);

        var member = result.Members.Should().ContainSingle().Subject;
        member.DeletedDocIds.Should().Equal("doc-b");
        member.DeletedContactIds.Should().BeEmpty(); // doc-other still sits on c2
        Received.InOrder(() =>
        {
            _ = _client.GetPurchaseDocumentAsync("doc-b", Arg.Any<CancellationToken>());
            _ = _client.DeletePurchaseDocumentAsync("doc-b", Arg.Any<CancellationToken>());
        });
        await _client.DidNotReceive().DeletePurchaseDocumentAsync("doc-other", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().DeletePurchaseDocumentAsync("doc-a", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_TwoReportsWithIdenticalLines_DeletesTheDuplicateUnderTheRightLine()
    {
        var report2 = Guid.NewGuid();
        var line2 = Booking("doc-b", report: report2);
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a"), line2]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a"), Doc("doc-b"), Doc("doc-b2")]);
        _client.GetPurchaseDocumentAsync("doc-b2", Arg.Any<CancellationToken>())
            .Returns(FullDoc("doc-b2", $"Expense report {report2}"));

        var result = await MakeService().RemoveHoldedDuplicatesAsync(Actor);

        result.Members.Single().DeletedDocIds.Should().Equal("doc-b2");
        await _audit.Received(1).LogAsync(AuditAction.HoldedDuplicateRemoved, "ExpenseLine", line2.LineId,
            Arg.Is<string>(d => d.Contains("doc-b2") && d.Contains("kept doc-b")), Actor, UserId, nameof(User));
    }

    [HumansFact]
    public async Task Apply_WhenAVerifyingGetFails_DeletesNothing()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a"), Doc("doc-b"), Doc("doc-c")]);
        _client.GetPurchaseDocumentAsync("doc-c", Arg.Any<CancellationToken>()).Throws(new HoldedTransientException("timeout"));

        var result = await MakeService().RemoveHoldedDuplicatesAsync(Actor);

        result.Error.Should().Contain("nothing was deleted");
        await _client.DidNotReceive().DeletePurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Apply_WithNothingToDelete_DeletesNothing()
    {
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns([Doc("doc-a")]);

        var result = await MakeService().RemoveHoldedDuplicatesAsync(Actor);

        result.Error.Should().Contain("nothing was deleted");
        await _client.DidNotReceive().DeletePurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _holded.DidNotReceive().SyncLedgerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ---- the nightly check ----------------------------------------------------------------------------

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sync_LogsAWarningWhenDuplicatesExist_AndZeroWhenNone(bool duplicates)
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new HoldedDocSyncState());
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCategoryMap>());
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>());
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns([Booking("doc-a")]);
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(duplicates ? [Doc("doc-a"), Doc("doc-b")] : [Doc("doc-a")]);
        var logger = new CapturingLogger<Service>();

        await MakeService(logger).SyncAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith("Holded duplicate check", StringComparison.Ordinal)).Subject;
        // The nightly line is the list-level rule only: a false positive there only refuses a payout.
        await _client.DidNotReceive().GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        if (duplicates)
        {
            entry.Level.Should().Be(LogLevel.Warning);
            entry.Message.Should().Contain("1 expense line(s)").And.Contain("1 extra document(s)").And.Contain("1 member(s)");
        }
        else
        {
            entry.Level.Should().Be(LogLevel.Information);
            entry.Message.Should().Contain("zero");
        }
    }

    private static HoldedAccountDto Account_(int number, decimal balance) => new()
    {
        Id = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Number = number,
        Name = "Creditor",
        Debit = 0m,
        Credit = 0m,
        Balance = balance,
    };
}
