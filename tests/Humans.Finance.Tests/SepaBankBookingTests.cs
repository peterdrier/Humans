using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Finance;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>
/// The bank line drives SEPA booking (nobodies-collective/Humans#1185): one test per acceptance
/// criterion, over the fake <see cref="IHoldedClient"/> the rest of this project already uses.
/// </summary>
public class SepaBankBookingTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 5, 1, 12, 0);

    /// <summary>The Sabadell line's booking date — deliberately not "today", because every posting
    /// this flow makes is dated the line, not the click.</summary>
    private static readonly LocalDate LineDate = new(2026, 4, 28);

    private static readonly Guid TransferId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Tag = "SEPA payout E11111111111111111111111111111111";
    private const string MovementId = "mov-1";
    private const string Remittance = "TRANSF 40000004 - NCA - ANA RUIZ";
    private const string AnaIban = "ES7921000813610123456789";
    private const string AnaIbanMasked = "ES79****789";
    private const int Account = 40000004;

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IBudgetServiceRead _budget = Substitute.For<IBudgetServiceRead>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly FakeClock _clock = new(FixedNow);
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly SepaOptions _sepa = new();
    private readonly Guid _userId = Guid.NewGuid();

    private Service MakeService() => new(
        _repo, _client, _budget, _holded, _clock, _cache, _audit,
        Substitute.For<IUserServiceRead>(), Substitute.For<IUserEmailService>(), Substitute.For<IEmailService>(),
        TestFinanceEmails.Create(), Options.Create(_sepa),
        NullLogger<Service>.Instance);

    public SepaBankBookingTests()
    {
        _sepa.CreditorName = "Nobodies Collective";
        _sepa.CreditorIban = "ES9121000418450200051332";
        _sepa.CreditorIdentifier = "G12345678901";
        _sepa.TreasuryAccountId = "treasury-1";
        _sepa.TreasuryLedgerAccount = 57200001;
        _client.IsConfigured.Returns(true);

        SeedTransfer(30m);
        SeedRows(Row(TransferId, _userId));
        SeedBinding(_userId);
        SeedMovements(Movement());
        SeedOwed(30m);
        SeedTaggedLines();
        SeedOpenDocs();
        _client.PayPurchaseDocumentAsync(
                Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<LocalDate>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => "pay-" + ci.ArgAt<string>(0));
        _client.PostLedgerEntryAsync(
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("e-1");
    }

    // ─── AC 1: many documents, and a retry that posts nothing twice ──────────────

    [HumansFact]
    public async Task Booking_TwentyFiveDocs_PaysThemAll_AndStampsTheRow()
    {
        // The column that used to hold the payment ids overflowed at about 20 — the reason for
        // nobodies-collective/Humans#1185. Nothing stores them now, so 25 is just 25.
        SeedTransfer(250m);
        SeedRows(Row(TransferId, _userId, amount: 250m));
        SeedMovements(Movement(amount: -250m));
        SeedOwed(250m);
        SeedOpenDocs(Enumerable.Range(1, 25).Select(i => Doc("d" + i, 10m, i)).ToArray());

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(25).PayPurchaseDocumentAsync(
            Arg.Any<string>(), 10m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!, default);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_RetriedAfterTheJournalEntryFailed_PostsOnlyTheDifference()
    {
        // Run 1: the €10 document payment lands, the €20 journal entry is refused. Nothing is
        // stamped, and the money that did move is audited.
        SeedOpenDocs(Doc("d1", 10m, 1));
        _client.PostLedgerEntryAsync(
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HoldedPermanentException("Holded 400"));

        var first = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        first.Succeeded.Should().BeFalse();
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransferBookingAsync(
            default, default, default, default!, default, default);
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("PARTIAL", StringComparison.Ordinal)
                                && d.Contains("pay-d1", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());

        // Run 2: Holded now shows the document settled and a €10 tagged line on the creditor
        // account, and the account owes €20 less. Only the €20 gap is posted, and only once.
        _client.ClearSubstitute(ClearOptions.CallActions | ClearOptions.ReturnValues);
        SeedMovements(Movement());
        SeedOwed(20m);
        SeedTaggedLines(TaggedLine(10m));
        SeedOpenDocs(Doc("d1", 0m, 1));
        _client.PostLedgerEntryAsync(
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("e-1");

        var second = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        second.Succeeded.Should().BeTrue();
        // Across both runs the document was paid exactly once — nothing is posted twice.
        await _client.Received(1).PayPurchaseDocumentAsync(
            "d1", 10m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        // Twice attempted — refused, then accepted — and for the €20 gap both times, never the
        // full €30 the pre-#1185 retry would have journalled a second time.
        await _client.Received(2).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 20m, Tag, Arg.Any<CancellationToken>());
        await _client.DidNotReceive().PostLedgerEntryAsync(
            Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), 30m, Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ResumedWhenEverythingIsAlreadyPosted_PostsNothing_AndStampsBooked()
    {
        // A run that crashed after the last posting: Holded already holds the whole €30 under this
        // transfer's tag. The retry posts nothing and just finishes the bookkeeping.
        SeedTaggedLines(TaggedLine(30m));
        SeedOwed(0m);
        SeedOpenDocs(Doc("d1", 30m, 1));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.DidNotReceiveWithAnyArgs().PayPurchaseDocumentAsync(
            default!, default, default, default, default!, default);
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!, default);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("resumed (30.00 EUR already posted)", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    // ─── AC 2: the live creditor balance ────────────────────────────────────────

    [HumansFact]
    public async Task Booking_LiveBalanceOwesLessThanTheTransfer_RefusesAndPostsNothing()
    {
        // Two files generated from one balance, or documents hand-paid in Holded in between.
        SeedOwed(20m);
        SeedOpenDocs(Doc("d1", 30m, 1));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("owes 20.00 EUR").And.Contain("30.00 EUR this transfer pays");
        await _client.DidNotReceiveWithAnyArgs().PayPurchaseDocumentAsync(
            default!, default, default, default, default!, default);
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!, default);
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransferBookingAsync(
            default, default, default, default!, default, default);
    }

    // ─── AC 3: dated by the bank line, and the line ends up reconciled ──────────

    [HumansFact]
    public async Task Booking_UsesTheBankLineDate_ForEveryPosting()
    {
        SeedOpenDocs(Doc("d1", 10m, 1));

        await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        await _client.Received(1).PayPurchaseDocumentAsync(
            "d1", 10m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        await _client.Received(1).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 20m, Tag, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ReconcilesTheBankLine_AgainstThePaidDocsAndTheEntry()
    {
        SeedOpenDocs(Doc("d1", 10m, 1));

        await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 2
                && d[0] == new HoldedReconcileDocumentRef("d1", HoldedReconcileDocumentType.Purchase)
                && d[1] == new HoldedReconcileDocumentRef("e-1", HoldedReconcileDocumentType.LedgerEntry)),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ReconcileRefusesTheJournalEntry_RetriesWithDocsOnly()
    {
        // Whether a daily-ledger entry is a valid reconcile target is the one unconfirmed piece of
        // the Holded API, so a refusal naming it falls back to the purchase documents alone.
        SeedOpenDocs(Doc("d1", 10m, 1));
        _client.ReconcileBankMovementAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d => d.Any(x =>
                    string.Equals(x.DocumentType, HoldedReconcileDocumentType.LedgerEntry, StringComparison.Ordinal))),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedPermanentException("Holded 400"));
        var actor = Guid.NewGuid();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, actor);

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 1 && string.Equals(d[0].DocumentId, "d1", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        // The dropped journal-entry remainder is part of this very line, so Holded still reads it
        // `pending`. Stamping ReconciledAt there would audit the booking as reconciled and drop the
        // row out of the sweep's pending re-check with the remainder unmatched.
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(
            default, default, default);
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILE PENDING", StringComparison.Ordinal)),
            actor, _userId, Arg.Any<string>());
    }

    [HumansFact]
    public async Task Booking_ReconcileRefusesTheJournalEntry_AndHoldedThenReadsReconciled_Stamps()
    {
        // The same fallback, with Holded reporting the line fully reconciled afterwards — the only
        // thing that may stamp ReconciledAt.
        SeedOpenDocs(Doc("d1", 10m, 1));
        _client.ReconcileBankMovementAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d => d.Any(x =>
                    string.Equals(x.DocumentType, HoldedReconcileDocumentType.LedgerEntry, StringComparison.Ordinal))),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedPermanentException("Holded 400"));
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<HoldedBankMovementDto>)
                [Movement(status: reads++ == 0 ? "pending" : "reconciled")]);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ReconcileFailsPermanently_StillBooks_AndLeavesReconcilePending()
    {
        SeedOpenDocs(Doc("d1", 10m, 1));
        _client.ReconcileBankMovementAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<HoldedReconcileDocumentRef>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedPermanentException("Holded 400"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        // The money moved and the row says so; only the tick in Holded is outstanding.
        result.Succeeded.Should().BeTrue();
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, null, Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(
            default, default, default);
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILE PENDING", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    // ─── AC 4 and 5: the shape of a partial payout, and a loan ──────────────────

    [HumansFact]
    public async Task Booking_PartialPayout_PaysOldestDocsFully_OneDocPartly_RestUntouched()
    {
        SeedOpenDocs(Doc("newest", 50m, 20), Doc("oldest", 12m, 1), Doc("middle", 40m, 10));

        await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        Received.InOrder(() =>
        {
            _ = _client.PayPurchaseDocumentAsync("oldest", 12m, "treasury-1", LineDate, Tag,
                Arg.Any<CancellationToken>());
            _ = _client.PayPurchaseDocumentAsync("middle", 18m, "treasury-1", LineDate, Tag,
                Arg.Any<CancellationToken>());
        });
        await _client.DidNotReceive().PayPurchaseDocumentAsync(
            "newest", Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!, default);
    }

    [HumansFact]
    public async Task Booking_BalanceWithNoDocuments_BooksOneJournalEntry()
    {
        // The loan case: the member put money in, so the balance has no purchase document behind it.
        SeedOpenDocs();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.DidNotReceiveWithAnyArgs().PayPurchaseDocumentAsync(
            default!, default, default, default, default!, default);
        await _client.Received(1).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 30m, Tag, Arg.Any<CancellationToken>());
    }

    // ─── AC 6: the Holded ids live in the audit entry, the row keeps the line ───

    [HumansFact]
    public async Task Booking_AuditEntry_CarriesEveryHoldedId_AndMasksTheIban()
    {
        SeedOpenDocs(Doc("d1", 10m, 1));
        var actor = Guid.NewGuid();

        await MakeService().BookSepaTransferAsync(TransferId, MovementId, actor);

        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("d1 10.00 EUR pay-d1", StringComparison.Ordinal)
                                && d.Contains("journal entry for 20.00 EUR (e-1)", StringComparison.Ordinal)
                                && d.Contains(MovementId, StringComparison.Ordinal)
                                && d.Contains("2026-04-28", StringComparison.Ordinal)
                                && d.Contains(AnaIbanMasked, StringComparison.Ordinal)
                                && !d.Contains(AnaIban, StringComparison.Ordinal)),
            actor, _userId, Arg.Any<string>());
    }

    [HumansFact]
    public async Task Booking_StampsBankMovementId_AndNoPaymentRefsExistAnyMore()
    {
        // The signature itself is the assertion: what is stamped is the bank line and the times,
        // never a list of Holded posting ids.
        SeedOpenDocs(Doc("d1", 30m, 1));
        var actor = Guid.NewGuid();

        await MakeService().BookSepaTransferAsync(TransferId, MovementId, actor);

        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, actor, MovementId, null, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    // ─── AC 7: a line nobody can place is surfaced, and nothing is posted ───────

    [HumansFact]
    public async Task Sweep_BankLineWithNoMatchingTransfer_PostsNothing()
    {
        SeedRows(Row(TransferId, _userId, amount: 99m));
        SeedTransfer(99m);

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Sweep_TwoUnbookedTransfersMatchOneLine_IsAmbiguous_PostsNothing()
    {
        var other = Guid.NewGuid();
        SeedRows(Row(TransferId, _userId), Row(other, Guid.NewGuid()));

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Sweep_MovementWhoseDescriptionNamesAnotherAccount_PostsNothing()
    {
        SeedMovements(Movement(description: "TRANSF 40000099 - NCA - OTRO"));

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await AssertNothingPosted();
    }

    // ─── The pairing the service re-validates, whoever posted it ────────────────

    [HumansFact]
    public async Task Booking_MovementAlreadyBookedToAnotherTransfer_Refuses()
    {
        var other = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId),
            Row(other, Guid.NewGuid(), bookedAt: FixedNow, movementId: MovementId));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("already booked another transfer");
        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Booking_AmountMismatch_Refuses()
    {
        SeedMovements(Movement(amount: -40m));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("not the 30.00 EUR this transfer pays");
        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Booking_MovementAlreadyReconciled_Refuses()
    {
        SeedMovements(Movement(status: "reconciled"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("already reconciled");
        await AssertNothingPosted();
    }

    // ─── The sweep itself ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task Sweep_BooksEveryMatchedTransfer_AndKeepsGoingPastOneRefusal()
    {
        // The first line's member lost their Holded binding; the second books normally.
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var secondUser = Guid.NewGuid();
        SeedRows(Row(TransferId, _userId), Row(second, secondUser, account: 40000007, amount: 45m));
        _repo.GetCreditorContactByUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns((HoldedCreditorContact?)null);
        SeedTransfer(45m, id: second, userId: secondUser, account: 40000007);
        SeedBinding(secondUser, account: 40000007);
        SeedMovements(
            Movement(),
            Movement(id: "mov-2", amount: -45m, description: "TRANSF 40000007 - NCA - BO"));
        SeedOwed(30m, (40000007, 45m));

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().SaveSepaTransferBookingAsync(
            TransferId, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<Instant?>(),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            second, FixedNow, null, "mov-2", Arg.Any<Instant?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_WhoseLineHoldedNowReportsReconciled_IsStamped()
    {
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId));
        SeedMovements(Movement(status: "reconciled"));

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILED", StringComparison.Ordinal)
                                && d.Contains(MovementId, StringComparison.Ordinal)),
            "sepa-bank-booking", _userId, Arg.Any<string>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_BookedDaysAfterItsLine_StillReadsBackFarEnough()
    {
        // The reconcile call failed when this was booked, 11 days after the file was generated. The
        // feed window has to reach the line's own date — keying it off BookedAt starts the read
        // after the line, so the sweep never sees the human's later reconcile and ReconciledAt and
        // its audit entry stay missing forever.
        var generatedAt = Instant.FromUtc(2026, 4, 19, 10, 0);
        var lineDate = new LocalDate(2026, 4, 19);
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow - Duration.FromDays(1),
            movementId: MovementId, generatedAt: generatedAt));
        // The stub honours the requested window, as the live feed does.
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? (IReadOnlyList<HoldedBankMovementDto>)[Movement(status: "reconciled", date: lineDate)]
                : []);

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_OlderThanTheFeedWindow_IsStillReadBack()
    {
        // The 90-day floor is about matching new transfers. A reconcile-pending row is already
        // booked against a known line, so the read has to reach that line however old it is —
        // otherwise a human's later reconcile is never seen and ReconciledAt plus its audit entry
        // stay missing forever.
        var generatedAt = FixedNow - Duration.FromDays(200);
        var lineDate = new LocalDate(2025, 10, 13);
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow - Duration.FromDays(199),
            movementId: MovementId, generatedAt: generatedAt));
        // The stub honours the requested window, as the live feed does.
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? (IReadOnlyList<HoldedBankMovementDto>)[Movement(status: "reconciled", date: lineDate)]
                : []);

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_NothingUnbooked_MakesNoHoldedCalls()
    {
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId,
            reconciledAt: FixedNow));

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().ListBankMovementsAsync(
            default!, default, default, default);
        await AssertNothingPosted();
    }

    // ─── H1-H3: the ways a booking could still over-post or over-claim ──────────

    [HumansFact]
    public async Task Booking_WhileAnotherBookingOfTheSameTransferRuns_PostsNothingASecondTime()
    {
        // Two callers (a Book click while the sweep runs, or a double-submitted form) are inside the
        // same transfer at once. The second must find the row booked, not an unbooked row and a
        // ledger that does not yet show the first run's postings.
        var booked = false;
        _repo.GetSepaTransferAsync(TransferId, Arg.Any<CancellationToken>())
            .Returns(_ => Transfer(30m, bookedAt: booked ? FixedNow : null));
        _repo.When(r => r.SaveSepaTransferBookingAsync(
                TransferId, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(),
                Arg.Any<Instant?>(), Arg.Any<CancellationToken>()))
            .Do(_ => booked = true);
        SeedOpenDocs(Doc("d1", 30m, 1));

        var service = MakeService();
        Task<SepaBookingResult>? second = null;
        var accounts = OwedAccounts(30m);
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (second is null)
            {
                // Mid-flight: the first booking has read "not booked" and has not stamped anything.
                second = Task.Run(() => MakeService().BookSepaTransferAsync(
                    TransferId, MovementId, Guid.NewGuid()));
                second.Wait(TimeSpan.FromMilliseconds(250));
                second.IsCompleted.Should().BeFalse("the second booking must wait for the first");
            }

            return accounts;
        });

        var first = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        first.Succeeded.Should().BeTrue();
        (await second!).Succeeded.Should().BeFalse();
        (await second!).Message.Should().Contain("already booked");
        await _client.Received(1).PayPurchaseDocumentAsync(
            "d1", 30m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ResumesFromAPostingDatedTheClick_NotTheBankLine()
    {
        // A pre-#1185 run dated its postings the day the treasurer clicked, which can be weeks off
        // the bank line. Read a window that misses it and the whole amount goes in a second time.
        var generatedOn = new LocalDate(2026, 3, 22);          // 40 days before "today"
        var clickDate = new LocalDate(2026, 4, 10);            // 18 days before the bank line
        SeedRows(Row(TransferId, _userId, generatedAt: generatedOn.AtMidnight().InUtc().ToInstant()));
        SeedLedgerWindow((clickDate, 20m));
        SeedOwed(10m);
        SeedOpenDocs();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        // Only the €10 gap — not the €30 the transfer pays.
        await _client.Received(1).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 10m, Tag, Arg.Any<CancellationToken>());
        await _client.DidNotReceive().PostLedgerEntryAsync(
            Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), 30m, Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ResumeThatCannotCoverTheGap_DoesNotStampBooked_AndAuditsTheShortfall()
    {
        // €5 is already posted and the account now owes only €10 — someone hand-paid the documents
        // in Holded. The run can post €10 of the outstanding €25, which is not a booking.
        SeedTaggedLines(TaggedLine(5m));
        SeedOwed(10m);
        SeedOpenDocs();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Only 15.00 EUR of 30.00 EUR").And.Contain("NOT booked");
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransferBookingAsync(
            default, default, default, default!, default, default);
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("SHORT SEPA booking", StringComparison.Ordinal)
                                && d.Contains("only 15.00 EUR is posted", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task Booking_TaggedCreditOnAnotherAccount_IsNotCountedAsAlreadyPosted()
    {
        // Our journal entry writes the same tag on both legs. If the account filter is ever not
        // honoured the two net to zero, "posted" reads 0, and everything is posted again.
        SeedTaggedLines(
            TaggedLine(30m),
            new HoldedLedgerLineDto
            {
                EntryNumber = 1,
                Line = 2,
                Date = FixedNow,
                AccountNum = 57200001,
                Debit = 0m,
                Credit = 30m,
                Description = Tag,
            });
        SeedOwed(0m);
        SeedOpenDocs();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!, default);
    }

    [HumansFact]
    public async Task Booking_RowSaveFails_AuditsThePostingsAndLeavesTheRowUnbooked()
    {
        // Real money has moved; the row does not say so. That must never be invisible.
        SeedOpenDocs(Doc("d1", 30m, 1));
        _repo.SaveSepaTransferBookingAsync(
                TransferId, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(),
                Arg.Any<Instant?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the database went away"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("could not be saved");
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("PARTIAL", StringComparison.Ordinal)
                                && d.Contains("pay-d1", StringComparison.Ordinal)
                                && d.Contains("could not be saved", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    // ─── AC 7, the page's half: what the treasurer is shown ─────────────────────

    [HumansFact]
    public async Task Page_MatchedLine_OffersItOnTheRow_WithItsDateAmountAndText()
    {
        SeedContacts(_userId);

        var (rows, _, unmatched, error) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        error.Should().BeNull();
        unmatched.Should().BeEmpty();
        var row = rows.Should().ContainSingle().Subject;
        row.CanBook.Should().BeTrue();
        row.CandidateBankMovementId.Should().Be(MovementId);
        row.CandidateBankMovementDate.Should().Be(LineDate);
        row.CandidateBankMovementAmount.Should().Be(-30m);
        row.CandidateBankMovementDescription.Should().Be(Remittance);
    }

    [HumansFact]
    public async Task Page_LineMatchingANotBookableTransfer_IsSurfacedInsteadOfVanishing()
    {
        // The row cannot be booked (the member lost their binding), so the line it matches must
        // still reach the "needs a human" panel rather than being swallowed by that row.
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<HoldedCreditorContact>());

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("no unbooked transfer");
    }

    [HumansFact]
    public async Task Page_LineNamingNoCreditorAccount_IsSurfacedForAHuman()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(description: "DOMICILIACION ENDESA"));

        var (_, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        var line = unmatched.Should().ContainSingle().Subject;
        line.ParsedAccountNum.Should().BeNull();
        line.Reason.Should().Contain("names no creditor account");
    }

    [HumansFact]
    public async Task Page_BankFeedUnreadable_StillListsTheRows_AndSaysSoOnce()
    {
        SeedContacts(_userId);
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedTransientException("Holded 503"));

        var (rows, unavailable, unmatched, error) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        unavailable.Should().BeNull();
        error.Should().Contain("bank feed could not be read");
        unmatched.Should().BeEmpty();
        rows.Should().ContainSingle().Which.CanBook.Should().BeFalse();
    }

    [HumansFact]
    public async Task Page_StaleUnbookedTransfer_DoesNotBlockANewerOne_AndSaysWhyItself()
    {
        // Same account, same amount, from a file generated outside the feed window: it used to make
        // every later transfer of that amount permanently ambiguous, with nothing to clear it.
        var stale = Guid.Parse("33333333-3333-3333-3333-333333333333");
        SeedRows(
            Row(TransferId, _userId),
            Row(stale, _userId, generatedAt: FixedNow - Duration.FromDays(200)));
        SeedContacts(_userId);

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        unmatched.Should().BeEmpty();
        rows.Single(r => r.TransferId == TransferId).CandidateBankMovementId.Should().Be(MovementId);
        rows.Single(r => r.TransferId == stale).NotBookableReason
            .Should().Contain("settle it in Holded by hand");
    }

    // ─── Review round 1: a line only pairs with a transfer it could actually have paid ──

    [HumansFact]
    public async Task Booking_LineDatedBeforeTheFileWasGenerated_Refuses()
    {
        // An older unreconciled payment of the same amount on the same creditor account. The money
        // it moved was not this transfer's — the file asking for this one did not exist yet.
        SeedMovements(Movement(date: new LocalDate(2026, 4, 20)));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("does not match this transfer");
        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Page_LineDatedBeforeTheFileWasGenerated_IsSurfacedForAHuman()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(date: new LocalDate(2026, 4, 20)));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("no unbooked transfer");
    }

    [HumansFact]
    public async Task Booking_MovementPartlyReconciled_Refuses()
    {
        // Somebody has already settled part of that line against documents by hand.
        SeedMovements(Movement(status: "partial"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("already partial");
        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Page_PartlyReconciledLine_IsNotOfferedAsACandidate()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(status: "partial"));

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
    }

    [HumansFact]
    public async Task Booking_TwoLinesCouldEachHavePaidTheTransfer_Refuses()
    {
        SeedMovements(Movement(), Movement(id: "mov-2"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("2 Sabadell lines could each have paid this transfer");
        await AssertNothingPosted();
    }

    [HumansFact]
    public async Task Page_TwoLinesMatchingOneTransfer_AreBothSurfaced_AndNeitherIsOffered()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(), Movement(id: "mov-2"));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
        unmatched.Should().HaveCount(2);
        unmatched.Should().OnlyContain(m => m.Reason.Contains("matches that transfer too"));
    }

    [HumansFact]
    public async Task Sweep_TwoLinesCouldEachHavePaidTheTransfer_BooksNeither()
    {
        SeedMovements(Movement(), Movement(id: "mov-2"));

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await AssertNothingPosted();
    }

    // ─── Review round 5: an unconfirmed entry, and the feed window's date slack ──

    [HumansTheory]
    [InlineData("partial", false)]
    [InlineData("reconciled", true)]
    public async Task Booking_JournalEntryUnconfirmed_StampsOnlyWhenHoldedThenReadsReconciled(
        string statusAfter, bool stamped)
    {
        // The entry's ref is unconfirmed, so the reconcile carries the purchase documents alone —
        // Holded accepting that can still leave the line `partial` with the remainder unmatched.
        SeedOpenDocs(Doc("d1", 10m, 1));
        _client.PostLedgerEntryAsync(
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("unconfirmed:e-1");
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<HoldedBankMovementDto>)
                [Movement(status: reads++ == 0 ? "pending" : statusAfter)]);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 1 && string.Equals(d[0].DocumentId, "d1", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repo.Received(stamped ? 1 : 0).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Page_TransferGeneratedAtTheWindowEdge_MatchesALineDatedInTheSlackBeforeIt()
    {
        // Generated exactly FeedWindowDays (90) ago, paid by a line GenerationSlackDays (2) earlier:
        // matching accepts that line, so the feed read has to reach it.
        SeedWindowEdgeTransfer();
        SeedContacts(_userId);

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(
            Xunit.TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().Be(MovementId);
    }

    [HumansFact]
    public async Task Sweep_TransferGeneratedAtTheWindowEdge_BooksALineDatedInTheSlackBeforeIt()
    {
        SeedWindowEdgeTransfer();

        await MakeService().RunAsync(Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, null, MovementId, Arg.Any<Instant?>(), Arg.Any<CancellationToken>());
    }

    private void SeedWindowEdgeTransfer()
    {
        var generatedAt = FixedNow - Duration.FromDays(90);
        var lineDate = new LocalDate(2026, 1, 29);
        SeedRows(Row(TransferId, _userId, generatedAt: generatedAt));
        // The stub honours the requested window, as the live feed does.
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? (IReadOnlyList<HoldedBankMovementDto>)[Movement(date: lineDate)]
                : []);
    }


    // ─── Seeding ────────────────────────────────────────────────────────────────

    private async Task AssertNothingPosted()
    {
        await _client.DidNotReceiveWithAnyArgs().PayPurchaseDocumentAsync(
            default!, default, default, default, default!, default);
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!, default);
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransferBookingAsync(
            default, default, default, default!, default, default);
    }

    private void SeedContacts(params Guid[] userIds) =>
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            userIds.Select(u => new HoldedCreditorContact
            {
                UserId = u,
                HoldedContactId = "c1",
                SupplierAccountNum = Account,
                Source = CreditorContactSource.Auto,
            }).ToList());

    /// <summary>Tagged lines the fake only returns when the window the service asked for contains
    /// them — what makes the width of that window testable.</summary>
    private void SeedLedgerWindow(params (LocalDate On, decimal Debit)[] lines) =>
        _client.ListLedgerEntriesAsync(
                Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var from = ci.ArgAt<LocalDate>(0);
                var to = ci.ArgAt<LocalDate>(1);
                return lines
                    .Where(l => l.On >= from && l.On <= to)
                    .Select(l => TaggedLine(l.Debit) with { Date = l.On.AtMidnight().InUtc().ToInstant() })
                    .ToList();
            });

    private static List<HoldedAccountDto> OwedAccounts(decimal owed) =>
    [
        new()
        {
            Id = "acc", Number = Account, Name = "Creditor",
            Debit = 0m, Credit = owed, Balance = -owed,
        },
    ];

    private SepaPayoutTransfer Transfer(decimal amount, Instant? bookedAt = null) =>
        new()
        {
            Id = TransferId,
            FileId = Guid.NewGuid(),
            UserId = _userId,
            SupplierAccountNum = Account,
            HoldedContactId = "c1",
            CreditorName = "Ana Ruiz",
            Iban = AnaIban,
            IbanMasked = AnaIbanMasked,
            Amount = amount,
            BookedAt = bookedAt,
        };

    private void SeedTransfer(
        decimal amount, Guid? id = null, Guid? userId = null, int account = Account) =>
        _repo.GetSepaTransferAsync(id ?? TransferId, Arg.Any<CancellationToken>()).Returns(
            new SepaPayoutTransfer
            {
                Id = id ?? TransferId,
                FileId = Guid.NewGuid(),
                UserId = userId ?? _userId,
                SupplierAccountNum = account,
                HoldedContactId = "c1",
                CreditorName = "Ana Ruiz",
                Iban = AnaIban,
                IbanMasked = AnaIbanMasked,
                Amount = amount,
            });

    private void SeedBinding(Guid userId, int account = Account) =>
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                UserId = userId,
                HoldedContactId = "c1",
                SupplierAccountNum = account,
                Source = CreditorContactSource.Auto,
            });

    private void SeedRows(params SepaPayoutTransferRow[] rows) =>
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(rows.ToList());

    private static SepaPayoutTransferRow Row(
        Guid id, Guid userId, int account = Account, decimal amount = 30m,
        Instant? bookedAt = null, string? movementId = null, Instant? reconciledAt = null,
        Instant? generatedAt = null) =>
        new(id, Guid.NewGuid(), "payout.xml", generatedAt ?? FixedNow - Duration.FromDays(3), Guid.NewGuid(),
            userId, account, "c1", "Ana Ruiz", AnaIbanMasked, amount, bookedAt, null, movementId,
            reconciledAt, null, null);

    private static HoldedBankMovementDto Movement(
        string id = MovementId, decimal amount = -30m, string? description = Remittance,
        string status = "pending", LocalDate? date = null) =>
        new()
        {
            Id = id,
            AccountId = "treasury-1",
            Date = date ?? LineDate,
            Amount = amount,
            Description = description,
            Status = status,
        };

    private void SeedMovements(params HoldedBankMovementDto[] movements) =>
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(movements.ToList());

    /// <summary>The live chart of accounts, as one owed amount per creditor account.</summary>
    private void SeedOwed(decimal owed, params (int Account, decimal Owed)[] others) =>
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(
            others.Prepend((Account: Account, Owed: owed))
                .Select(a => new HoldedAccountDto
                {
                    Id = "acc-" + a.Account,
                    Number = a.Account,
                    Name = "Creditor",
                    Debit = 0m,
                    Credit = a.Owed,
                    Balance = -a.Owed,
                })
                .ToList());

    /// <summary>A ledger line this transfer's tag already put on the creditor account — what a
    /// crashed earlier run left behind.</summary>
    private static HoldedLedgerLineDto TaggedLine(decimal debit) =>
        new()
        {
            EntryNumber = 1,
            Line = 1,
            Date = FixedNow,
            AccountNum = Account,
            Debit = debit,
            Credit = 0m,
            Description = Tag,
        };

    private void SeedTaggedLines(params HoldedLedgerLineDto[] lines) =>
        _client.ListLedgerEntriesAsync(
                Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(lines.ToList());

    private static HoldedPurchaseDocListItemDto Doc(
        string id, decimal pending, int dayOfMonth, string contactId = "c1") =>
        new()
        {
            Id = id,
            DocNumber = id.ToUpperInvariant(),
            ContactId = contactId,
            ContactName = "Ana Ruiz",
            Date = Instant.FromUtc(2026, 4, dayOfMonth, 0, 0),
            Subtotal = pending,
            Tax = 0m,
            Total = pending,
            PaymentsPending = pending,
            IsDraft = false,
        };

    private void SeedOpenDocs(params HoldedPurchaseDocListItemDto[] docs) =>
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(docs.ToList());
}
