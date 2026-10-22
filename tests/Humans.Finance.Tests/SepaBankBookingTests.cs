using Humans.Base.Caching;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
/// One test per acceptance criterion of bank-line-driven SEPA booking, over the fake
/// <see cref="IHoldedClient"/> the rest of this project already uses.
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
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly FakeClock _clock = new(FixedNow);
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly TrackedCache<string, Dictionary<string, int>> _bookingState = new(
        "Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance);
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly SepaOptions _sepa = new();
    private readonly Guid _userId = Guid.NewGuid();

    private Service MakeService() => MakeService(NullLogger<Service>.Instance);

    private Service MakeService(ILogger<Service> logger) => new(
        _repo, _client, _budget, _holded, Substitute.For<IExpenseHoldedBookingService>(), _clock, _cache, _audit,
        Substitute.For<IUserService>(), Substitute.For<IUserEmailService>(), Substitute.For<IEmailService>(),
        TestFinanceEmails.Create(), Options.Create(_sepa), _bookingState, logger);

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

    [HumansFact]
    public async Task Booking_TwentyFiveDocs_PaysThemAll_AndStampsTheRow()
    {
        // 25 ids, past the ~20 an earlier bounded column overflowed at: every id must be on the
        // row for the sweep to re-issue the reconcile from.
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
            default, default, default, default, default!);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(),
            Arg.Is<string?>(r => r!.Split(',', StringSplitOptions.None).Length == 25
                                 && r.StartsWith("pay-d1,", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_RetriedAfterTheJournalEntryFailed_RefusesUntilAHumanSettlesIt()
    {
        // Run 1: the €10 document payment lands, the €20 journal entry is refused. Nothing is
        // stamped, the money that did move is audited, and its payment id is written to the row.
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
        await _repo.Received(1).SaveSepaTransferPaymentRefsAsync(
            TransferId, "pay-d1", Arg.Any<CancellationToken>());

        // Run 2: Holded discards the description on a purchase payment, so the tag sum reads 0 and
        // the €10 that moved is invisible to the retry — it would post the whole €30 again as a
        // journal entry. The row now carries the payment id, and the retry refuses on it.
        _client.ClearSubstitute(ClearOptions.CallActions | ClearOptions.ReturnValues);
        SeedTransfer(30m, paymentRefs: "pay-d1");
        SeedMovements(Movement());
        SeedOwed(20m);
        SeedOpenDocs(Doc("d1", 0m, 1));

        var second = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        second.Succeeded.Should().BeFalse();
        second.Message.Should().Contain("pay-d1").And.Contain("Settle it in Holded by hand");
        // Across both runs: the one payment of run 1, and never the journal entry run 2 would have
        // posted for the whole €30.
        await _client.Received(1).PayPurchaseDocumentAsync(
            "d1", 10m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        await _client.DidNotReceive().PostLedgerEntryAsync(
            Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), 30m, Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransferBookingAsync(
            default, default, default, default!, default, default);
    }

    [HumansFact]
    public async Task Page_TransferWithPaymentsLandedButNotBooked_IsNotOffered_AndSaysWhy()
    {
        SeedRows(Row(TransferId, _userId, paymentRefs: "pay-d1,unconfirmed:d2"));
        SeedContacts(_userId);

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.CanBook.Should().BeFalse();
        row.NotBookableReason.Should().Contain("pay-d1").And.Contain("settle it in Holded by hand");
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
            default!, default, default, default, default!);
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("resumed (30.00 EUR already posted)", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task Booking_LiveBalanceOwesLessThanTheTransfer_BooksTheWholeTransfer_AndAuditsTheOverpayment()
    {
        // Two files generated from one balance, or documents hand-paid in Holded in between. The
        // file went to the bank and the money left, so the whole €30 is booked regardless: the one
        // open €20 document, then a €10 journal entry that leaves the account in credit.
        SeedOwed(20m);
        SeedOpenDocs(Doc("d1", 20m, 1));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).PayPurchaseDocumentAsync(
            "d1", 20m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        await _client.Received(1).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 10m, Tag, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("OVERPAID", StringComparison.Ordinal)
                                && d.Contains("owed 20.00 EUR", StringComparison.Ordinal)
                                && d.Contains("10.00 EUR in credit", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task Booking_LiveBalanceCoversTheTransfer_AuditsNoOverpayment()
    {
        SeedOwed(30m);
        SeedOpenDocs(Doc("d1", 30m, 1));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _audit.DidNotReceive().LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("OVERPAID", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

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
    public async Task Booking_ReconcilesTheBankLine_AgainstThePaymentIds_NeverTheDocuments()
    {
        // Production 2026-10-17: reconciling with the purchase document ids made Holded mint a
        // second payment for the whole line on the first document (a 75.00 line, 150.00 of debits).
        // The payload names the payments POST purchases/{id}/payments returned, and nothing else —
        // the journal entry cannot be linked to a bank line by API.
        SeedOpenDocs(Doc("d1", 10m, 1));
        var actor = Guid.NewGuid();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, actor);

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 1
                && string.Equals(d[0].DocumentId, "pay-d1", StringComparison.Ordinal)
                && string.Equals(d[0].DocumentType, "payment", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _client.DidNotReceive().ReconcileBankMovementAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d => d.Any(x =>
                string.Equals(x.DocumentType, "purchase", StringComparison.Ordinal)
                || string.Equals(x.DocumentId, "d1", StringComparison.Ordinal)
                || string.Equals(x.DocumentId, "e-1", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
        // The €20 remainder is recorded as an `entry:` token next to the payment id — never sent in
        // a reconcile, but what lets the row say how much a human has to match in Holded.
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, actor, MovementId, null, "pay-d1,entry:20.00", Arg.Any<CancellationToken>());
        // €20 of the line went to the journal entry, which the reconcile could not name: Holded
        // reads the line `pending` on the re-read, so the row stays reconcile pending and says why.
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(
            default, default);
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILE PENDING", StringComparison.Ordinal)
                                && d.Contains("journal entry cannot be linked", StringComparison.Ordinal)),
            actor, _userId, Arg.Any<string>());
    }

    [HumansFact]
    public async Task Booking_PaidEntirelyByDocuments_StampsWhenHoldedAcceptsTheReconcile()
    {
        SeedOpenDocs(Doc("d1", 10m, 1), Doc("d2", 20m, 2));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 2
                && string.Equals(d[0].DocumentId, "pay-d1", StringComparison.Ordinal)
                && string.Equals(d[1].DocumentId, "pay-d2", StringComparison.Ordinal)
                && d.All(x => string.Equals(x.DocumentType, HoldedReconcileDocumentType.Payment, StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_WithAHundredPayments_SendsTheReconcileInOneCall_AndStampsOnAcceptance()
    {
        // Nothing about the endpoint has been exercised above a handful of ids, so the one-call form
        // goes first; when Holded takes it, acceptance is the reconcile as for any short list.
        SeedTransfer(1000m);
        SeedRows(Row(TransferId, _userId, amount: 1000m));
        SeedMovements(Movement(amount: -1000m));
        SeedOwed(1000m);
        SeedOpenDocs(Enumerable.Range(1, 100).Select(i => Doc("d" + i, 10m, i)).ToArray());

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d => d.Count == 100),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("reconciled", true)]
    [InlineData("partial", false)]
    public async Task Booking_LongListHoldedRefuses_FallsBackToChunks_AndStampsOnlyWhenTheLineReadsReconciled(
        string statusAfter, bool stamped)
    {
        // Holded 4xxs the 120-id call. A refusal links nothing, so the same ids go again in chunks of
        // 50 against the same line; after chunks only Holded saying `reconciled` stamps the row.
        SeedTransfer(1200m);
        SeedRows(Row(TransferId, _userId, amount: 1200m));
        SeedOwed(1200m);
        SeedOpenDocs(Enumerable.Range(1, 120).Select(i => Doc("d" + i, 10m, i)).ToArray());
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(amount: -1200m, status: reads++ == 0 ? "pending" : statusAfter)]);
        var sent = new List<int>();
        _client.ReconcileBankMovementAsync(
                "treasury-1", MovementId, Arg.Do<IReadOnlyList<HoldedReconcileDocumentRef>>(d => sent.Add(d.Count)),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<HoldedReconcileDocumentRef>>().Count > 50
                ? Task.FromException(new HoldedPermanentException(400, "too many", "Holded refused"))
                : Task.CompletedTask);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        sent.Should().Equal(120, 50, 50, 20);
        await _repo.Received(stamped ? 1 : 0).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_AChunkIsRefused_TheRestStillGo_AndTheRefusedIdsAreAHumans()
    {
        // 110 payments → one call (refused), then chunks of 50, 50, 10. Chunk 2 is refused; chunk 3
        // still goes, so one bad id strands nothing behind it. The chunks through left the line
        // `partial`, which the sweep never re-issues: the refused ids are logged for a human, the row
        // is booked (the money moved) but not stamped.
        SeedTransfer(1100m);
        SeedRows(Row(TransferId, _userId, amount: 1100m));
        SeedOwed(1100m);
        SeedOpenDocs(Enumerable.Range(1, 110).Select(i => Doc("d" + i, 10m, i)).ToArray());
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(amount: -1100m, status: reads++ == 0 ? "pending" : "partial")]);
        var sent = new List<int>();
        _client.ReconcileBankMovementAsync(
                "treasury-1", MovementId, Arg.Do<IReadOnlyList<HoldedReconcileDocumentRef>>(d => sent.Add(d.Count)),
                Arg.Any<CancellationToken>())
            .Returns(_ => sent.Count is 1 or 3
                ? Task.FromException(new HoldedPermanentException(400, "no", "Holded refused"))
                : Task.CompletedTask);
        var logger = new CapturingLogger<Service>();

        var result = await MakeService(logger).BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        sent.Should().Equal(110, 50, 50, 10);
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(default, default);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, null,
            Arg.Is<string?>(r => r!.Split(',', StringSplitOptions.None).Length == 110),
            Arg.Any<CancellationToken>());
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("linked 60 of 110", StringComparison.Ordinal)
            && e.Message.Contains("refused 50: pay-d51,", StringComparison.Ordinal)
            && e.Message.Contains("pay-d100.", StringComparison.Ordinal)
            && e.Message.Contains("by hand", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Sweep_LineLeftPartialByARefusedChunk_IsNotReissued()
    {
        // The row a refused chunk left behind: booked, refs recorded, line `partial`. The sweep reads
        // the line and leaves it alone — re-sending ids Holded already linked is not a re-issue.
        var refs = string.Join(",", Enumerable.Range(1, 110).Select(i => "pay-" + i));
        SeedRows(Row(TransferId, _userId, amount: 1100m, bookedAt: FixedNow, movementId: MovementId, paymentRefs: refs));
        SeedMovements(Movement(amount: -1100m, status: "partial"));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(default!, default!, default!);
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(default, default);
    }

    [HumansFact]
    public async Task Booking_LineStillPartialAfterEveryPaymentWasNamed_IsLoggedAsAWarning()
    {
        // No remainder, no sentinel: every euro of the line was named, and Holded still reads the
        // line `partial`. That is a real finding, not a known limit — a Warning naming the line,
        // the amount and the ids sent, and the row stays pending for a human.
        SeedTransfer(600m);
        SeedRows(Row(TransferId, _userId, amount: 600m));
        SeedOwed(600m);
        SeedOpenDocs(Enumerable.Range(1, 60).Select(i => Doc("d" + i, 10m, i)).ToArray());
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(amount: -600m, status: reads++ == 0 ? "pending" : "partial")]);
        _client.ReconcileBankMovementAsync(
                "treasury-1", MovementId, Arg.Any<IReadOnlyList<HoldedReconcileDocumentRef>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<HoldedReconcileDocumentRef>>().Count > 50
                ? Task.FromException(new HoldedPermanentException(400, "too many", "Holded refused"))
                : Task.CompletedTask);
        var logger = new CapturingLogger<Service>();

        var result = await MakeService(logger).BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(default, default);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(MovementId, StringComparison.Ordinal)
            && e.Message.Contains("600.00 EUR", StringComparison.Ordinal)
            && e.Message.Contains("partial", StringComparison.Ordinal)
            && e.Message.Contains("pay-d1,", StringComparison.Ordinal)
            && e.Message.Contains("pay-d60", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_WithALongList_ReissuesThroughTheSameChunkedPath()
    {
        // The sweep's re-issue is the same call as the booking's: one call first, chunks when Holded
        // refuses the long list, stamp only on `reconciled`.
        var refs = string.Join(",", Enumerable.Range(1, 70).Select(i => "pay-" + i));
        SeedRows(Row(TransferId, _userId, amount: 700m, bookedAt: FixedNow, movementId: MovementId, paymentRefs: refs));
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(amount: -700m, status: reads++ == 0 ? "pending" : "reconciled")]);
        var sent = new List<int>();
        _client.ReconcileBankMovementAsync(
                "treasury-1", MovementId, Arg.Do<IReadOnlyList<HoldedReconcileDocumentRef>>(d => sent.Add(d.Count)),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<HoldedReconcileDocumentRef>>().Count > 50
                ? Task.FromException(new HoldedPermanentException(400, "too many", "Holded refused"))
                : Task.CompletedTask);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        sent.Should().Equal(70, 50, 20);
        await _repo.Received(1).MarkSepaTransferReconciledAsync(TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_NeverSendsTheJournalEntryToken()
    {
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId, paymentRefs: "pay-1,entry:20.00"));
        SeedMovements(Movement());

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 1 && string.Equals(d[0].DocumentId, "pay-1", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void Row_JournalEntryAmount_ReadsTheEntryToken_AndIsNullWithoutOne()
    {
        Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId, paymentRefs: "pay-1,entry:20.00")
            .JournalEntryAmount.Should().Be(20.00m);
        Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId, paymentRefs: "pay-1,unconfirmed:d2")
            .JournalEntryAmount.Should().BeNull();
        Row(TransferId, _userId).JournalEntryAmount.Should().BeNull();
    }

    [HumansFact]
    public async Task Booking_WithAJournalRemainder_StampsOnlyWhenHoldedThenReadsReconciled()
    {
        // Same booking as above, with Holded reporting the line fully reconciled afterwards (a human
        // linked the entry in the GUI between the call and the re-read) — the only thing that may
        // stamp ReconciledAt.
        SeedOpenDocs(Doc("d1", 10m, 1));
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(status: reads++ == 0 ? "pending" : "reconciled")]);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("partial", false)]
    [InlineData("reconciled", true)]
    public async Task Booking_PaymentRefUnconfirmed_IsLeftOutOfTheReconcile_NeverReplacedByTheDocumentId(
        string statusAfter, bool stamped)
    {
        SeedOpenDocs(Doc("d1", 10m, 1), Doc("d2", 20m, 2));
        _client.PayPurchaseDocumentAsync(
                "d1", Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<LocalDate>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("unconfirmed:d1");
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(status: reads++ == 0 ? "pending" : statusAfter)]);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 1 && string.Equals(d[0].DocumentId, "pay-d2", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        // The sentinel is kept on the row for the record; the sweep filters it the same way.
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, null, "unconfirmed:d1,pay-d2",
            Arg.Any<CancellationToken>());
        await _repo.Received(stamped ? 1 : 0).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_EveryPaymentRefUnconfirmed_SendsNoReconcile_AndStaysPending()
    {
        SeedOpenDocs(Doc("d1", 30m, 1));
        _client.PayPurchaseDocumentAsync(
                Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<LocalDate>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("unconfirmed:d1");

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(
            default!, default!, default!);
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(
            default, default);
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
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, null, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(
            default, default);
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILE PENDING", StringComparison.Ordinal)),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>());
    }

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
            default, default, default, default, default!);
    }

    [HumansFact]
    public async Task Booking_BalanceWithNoDocuments_BooksOneJournalEntry()
    {
        // The loan case: the member put money in, so the balance has no purchase document behind it.
        SeedOpenDocs();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.DidNotReceiveWithAnyArgs().PayPurchaseDocumentAsync(
            default!, default, default, default, default!);
        await _client.Received(1).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 30m, Tag, Arg.Any<CancellationToken>());
    }

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
    public async Task Booking_StampsBankMovementId_AndThePaymentIds()
    {
        // The bank line is the record of what paid the transfer; the payment ids are what the
        // reconcile — now or the sweep's re-issue — names to Holded.
        SeedOpenDocs(Doc("d1", 30m, 1));
        var actor = Guid.NewGuid();

        await MakeService().BookSepaTransferAsync(TransferId, MovementId, actor);

        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, actor, MovementId, null, "pay-d1", Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_BankLineWithNoMatchingTransfer_PostsNothing()
    {
        SeedRows(Row(TransferId, _userId, amount: 99m));
        SeedTransfer(99m);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Sweep_TwoUnbookedTransfersMatchOneLine_IsAmbiguous_PostsNothing()
    {
        var other = Guid.NewGuid();
        SeedRows(Row(TransferId, _userId), Row(other, Guid.NewGuid()));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Sweep_MovementWhoseDescriptionNamesAnotherAccount_PostsNothing()
    {
        SeedMovements(Movement(description: "TRANSF 40000099 - NCA - OTRO"));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
    }

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
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Booking_AmountMismatch_Refuses()
    {
        SeedMovements(Movement(amount: -40m));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("not the 30.00 EUR this transfer pays");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Booking_MovementAlreadyReconciled_Refuses()
    {
        SeedMovements(Movement(status: "reconciled"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("already reconciled");
        await AssertNothingPostedAsync();
    }

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

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().SaveSepaTransferBookingAsync(
            TransferId, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<Instant?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            second, FixedNow, null, "mov-2", Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_WhoseLineHoldedNowReportsReconciled_IsStamped()
    {
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId));
        SeedMovements(Movement(status: "reconciled"));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILED", StringComparison.Ordinal)
                                && d.Contains(MovementId, StringComparison.Ordinal)),
            "sepa-bank-booking", _userId, Arg.Any<string>());
    }

    [HumansTheory]
    [InlineData("reconciled", true)]
    [InlineData("partial", false)]
    public async Task Sweep_ReconcilePendingRow_ReissuesTheReconcile_FromItsPaymentIds(
        string statusAfter, bool stamped)
    {
        // Booked while the token could only read the bank feed: the reconcile 403'd, the row has
        // been pending since. Re-reading the line's status alone would leave it so forever.
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId,
            paymentRefs: "pay-1,unconfirmed:d2,pay-3"));
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(status: reads++ == 0 ? "pending" : statusAfter)]);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 2
                && string.Equals(d[0].DocumentId, "pay-1", StringComparison.Ordinal)
                && string.Equals(d[1].DocumentId, "pay-3", StringComparison.Ordinal)
                && d.All(x => string.Equals(x.DocumentType, HoldedReconcileDocumentType.Payment, StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
        await _repo.Received(stamped ? 1 : 0).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
        await _audit.Received(stamped ? 1 : 0).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("RECONCILED", StringComparison.Ordinal)),
            "sepa-bank-booking", _userId, Arg.Any<string>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRowsOfOneProcessedFile_AreReissuedTogether()
    {
        var second = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId, paymentRefs: "pay-1"),
            Row(second, Guid.NewGuid(), bookedAt: FixedNow, movementId: MovementId, paymentRefs: "pay-2"));
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(amount: -60m, status: reads++ == 0 ? "pending" : "reconciled")]);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 2
                && string.Equals(d[0].DocumentId, "pay-1", StringComparison.Ordinal)
                && string.Equals(d[1].DocumentId, "pay-2", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(TransferId, FixedNow, Arg.Any<CancellationToken>());
        await _repo.Received(1).MarkSepaTransferReconciledAsync(second, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRowOfAHalfProcessedFile_IsNotReissued()
    {
        // Process booked one transfer of the file and stopped before the other. Linking that one
        // payment would leave the line `partial`, and the Process retry refuses a touched line —
        // so the sweep leaves the line alone until the file is finished.
        var fileId = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId, fileId: fileId, bookedAt: FixedNow, movementId: MovementId, paymentRefs: "pay-1"),
            Row(Guid.NewGuid(), Guid.NewGuid(), account: Account + 1, fileId: fileId));
        SeedMovements(Movement(amount: -60m));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(
            default!, default!, default!);
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(default, default);
    }

    [HumansTheory]
    [InlineData("pending", null)]
    [InlineData("partial", "pay-1")]
    public async Task Sweep_ReconcilePendingRow_WithNothingToReissue_OnlyReadsTheLine(
        string status, string? paymentRefs)
    {
        // No payment id on the row (booked before the ids were kept, or paid by a journal entry
        // alone), or a line already `partial` — its payments are linked and the rest is a human's.
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId, paymentRefs: paymentRefs));
        SeedMovements(Movement(status: status));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(
            default!, default!, default!);
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(default, default);
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_BookedDaysAfterItsLine_StillReadsBackFarEnough()
    {
        // Booked 11 days after the file was generated, when the reconcile failed. The feed window
        // must reach the line's own date: keyed off BookedAt it starts after the line, and
        // ReconciledAt and its audit entry stay missing forever.
        var generatedAt = Instant.FromUtc(2026, 4, 19, 10, 0);
        var lineDate = new LocalDate(2026, 4, 19);
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow - Duration.FromDays(1),
            movementId: MovementId, generatedAt: generatedAt));
        // The stub honours the requested window, as the live feed does.
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? [Movement(status: "reconciled", date: lineDate)]
                : []);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_ReconcilePendingRow_OlderThanTheFeedWindow_IsStillReadBack()
    {
        // The 90-day floor is for matching new transfers. A reconcile-pending row is already booked
        // against a known line, so the read must reach that line however old it is, or a human's
        // later reconcile is never seen.
        var generatedAt = FixedNow - Duration.FromDays(200);
        var lineDate = new LocalDate(2025, 10, 13);
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow - Duration.FromDays(199),
            movementId: MovementId, generatedAt: generatedAt));
        // The stub honours the requested window, as the live feed does.
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? [Movement(status: "reconciled", date: lineDate)]
                : []);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            TransferId, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_NothingUnbooked_MakesNoHoldedCalls()
    {
        SeedRows(Row(TransferId, _userId, bookedAt: FixedNow, movementId: MovementId,
            reconciledAt: FixedNow));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _client.DidNotReceiveWithAnyArgs().ListBankMovementsAsync(
            default!, default, default);
        await AssertNothingPostedAsync();
    }

    /// <summary>A feed where <paramref name="stuckIds"/> are pending outgoing lines with no readable
    /// date: the client fails closed on the first one unless it was told to skip it.</summary>
    private void SeedStuckFeed(params string[] stuckIds) =>
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci =>
            {
                var skip = ci.ArgAt<IReadOnlySet<string>?>(4);
                var stuck = stuckIds.FirstOrDefault(id => skip is null || !skip.Contains(id));
                return stuck is null
                    ? Task.FromResult<IReadOnlyList<HoldedBankMovementDto>>([Movement()])
                    : Task.FromException<IReadOnlyList<HoldedBankMovementDto>>(
                        new HoldedPermanentException("no readable date", new FormatException(), stuck));
            });

    private async Task RunSweepAsync(int times)
    {
        for (var i = 0; i < times; i++)
            await MakeService().RunAsync(TestContext.Current.CancellationToken);
    }

    [HumansFact]
    public async Task Sweep_LineStuckForFewerThanThreeRuns_StillBlocksTheWholeFeed()
    {
        SeedStuckFeed("stuck-1");

        await RunSweepAsync(2);

        await AssertNothingPostedAsync();
        await _audit.DidNotReceive().LogAsync(
            AuditAction.SepaBankLineSkipped, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Sweep_LineStuckForThreeRuns_IsSkipped_TheRestBooks_AndItIsAudited()
    {
        SeedStuckFeed("stuck-1");

        await RunSweepAsync(2);
        await AssertNothingPostedAsync();
        await RunSweepAsync(1);

        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, null, MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaBankLineSkipped, "SepaBankLine", Guid.Empty,
            Arg.Is<string>(d => d.Contains("stuck-1", StringComparison.Ordinal)),
            "sepa-bank-booking", Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Sweep_SkippedLine_IsAuditedOnce_NotOnEveryLaterRun()
    {
        SeedStuckFeed("stuck-1");

        await RunSweepAsync(5);

        await _audit.Received(1).LogAsync(
            AuditAction.SepaBankLineSkipped, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Sweep_ReReadFailsAfterThresholdSkip_NextRunDoesNotAuditTheSkipAgain()
    {
        SeedStuckFeed("stuck-1");
        await RunSweepAsync(2);
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci => ci.ArgAt<IReadOnlySet<string>?>(4) is { Count: > 0 }
                ? Task.FromException<IReadOnlyList<HoldedBankMovementDto>>(new HoldedTransientException("down"))
                : Task.FromException<IReadOnlyList<HoldedBankMovementDto>>(
                    new HoldedPermanentException("no readable date", new FormatException(), "stuck-1")));

        await RunSweepAsync(2);

        await _audit.Received(1).LogAsync(
            AuditAction.SepaBankLineSkipped, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Sweep_DifferentStuckLine_RestartsTheCount()
    {
        SeedStuckFeed("stuck-1");
        await RunSweepAsync(2);
        SeedStuckFeed("stuck-2");

        await RunSweepAsync(2);

        await AssertNothingPostedAsync();
        SeedStuckFeed("stuck-2");
        await RunSweepAsync(1);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, null, MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_LineThatReadsAgain_ResetsTheCount()
    {
        SeedStuckFeed("stuck-1");
        await RunSweepAsync(2);
        SeedMovements(Movement());
        await RunSweepAsync(1);
        SeedStuckFeed("stuck-1");

        await RunSweepAsync(2);

        await _audit.DidNotReceive().LogAsync(
            AuditAction.SepaBankLineSkipped, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

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
                Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(_ => booked = true);
        SeedOpenDocs(Doc("d1", 30m, 1));

        var service = MakeService();
        Task<SepaBookingResult>? second = null;
        var accounts = OwedAccounts(30m);
        _client.ListAccountingAccountsAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (second is null)
            {
                // Mid-flight: the first booking has read "not booked" and has not stamped anything.
                second = Task.Run(() => MakeService().BookSepaTransferAsync(
                    TransferId, MovementId, Guid.NewGuid()));
                await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(250)));
                second.IsCompleted.Should().BeFalse("the second booking must wait for the first");
            }

            return (IReadOnlyList<HoldedAccountDto>)accounts;
        });

        var first = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        first.Succeeded.Should().BeTrue();
        (await second!).Succeeded.Should().BeFalse();
        (await second!).Message.Should().Contain("already booked");
        await _client.Received(1).PayPurchaseDocumentAsync(
            "d1", 30m, "treasury-1", LineDate, Tag, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_ResumesFromAPostingDatedTheClick_NotTheBankLine()
    {
        // An older run dated its postings the day the treasurer clicked, which can be weeks off
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
    public async Task Booking_ResumeBeyondWhatTheAccountOwes_PostsTheWholeGap_AndStampsBooked()
    {
        // €5 is already posted and the account now owes only €10 — someone hand-paid the documents
        // in Holded. The money left the bank all the same: the outstanding €25 is posted as one
        // journal entry, the row is booked, and the audit entry says the account is now €15 in credit.
        SeedTaggedLines(TaggedLine(5m));
        SeedOwed(10m);
        SeedOpenDocs();

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).PostLedgerEntryAsync(
            LineDate, Account, 57200001, 25m, Tag, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SepaPayoutTransferBooked, Arg.Any<string>(), TransferId,
            Arg.Is<string>(d => d.Contains("resumed (5.00 EUR already posted)", StringComparison.Ordinal)
                                && d.Contains("15.00 EUR in credit", StringComparison.Ordinal)),
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
            default, default, default, default, default!);
    }

    [HumansFact]
    public async Task Booking_RowSaveFails_AuditsThePostingsAndLeavesTheRowUnbooked()
    {
        // Real money has moved; the row does not say so. That must never be invisible.
        SeedOpenDocs(Doc("d1", 30m, 1));
        _repo.SaveSepaTransferBookingAsync(
                TransferId, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(),
                Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

    [HumansFact]
    public async Task Page_MatchedLine_OffersItOnTheRow_WithItsDateAmountAndText()
    {
        SeedContacts(_userId);

        var (rows, _, unmatched, error) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

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

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
        unmatched.Should().ContainSingle().Which.Reason.Should()
            .Contain("cannot be booked").And.Contain("no Holded contact binding");
    }

    [HumansFact]
    public async Task Page_LineNamingNoCreditorAccount_AndNoTransferOfItsAmount_IsSurfacedForAHuman()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(amount: -12.5m, description: "DOMICILIACION ENDESA"));

        var (_, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        var line = unmatched.Should().ContainSingle().Subject;
        line.ParsedAccountNum.Should().BeNull();
        line.Reason.Should().Contain("names no creditor account").And.Contain("no unbooked transfer is for");
    }

    private const string Textless = "TRANSFERENCIA EMITIDA";
    private const int OtherAccount = 40000007;

    [HumansFact]
    public async Task Page_TextlessLine_PairsTheOneTransferOfItsAmount_AndSaysItMatchedOnAmountOnly()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(description: Textless));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        unmatched.Should().BeEmpty();
        var row = rows.Should().ContainSingle().Subject;
        row.CanBook.Should().BeTrue();
        row.CandidateBankMovementId.Should().Be(MovementId);
        row.CandidateMatchedByText.Should().BeFalse();
    }

    [HumansFact]
    public async Task Page_TextMatchedLine_SaysSo()
    {
        SeedContacts(_userId);

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateMatchedByText.Should().BeTrue();
    }

    [HumansFact]
    public async Task Page_TextlessLine_TwoUnbookedTransfersOfItsAmount_IsFlagged_AndNeitherIsOffered()
    {
        var otherUser = Guid.NewGuid();
        SeedRows(Row(TransferId, _userId), Row(Guid.NewGuid(), otherUser));
        SeedContacts(_userId, otherUser);
        SeedMovements(Movement(description: Textless));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows.Should().OnlyContain(r => !r.CanBook);
        unmatched.Should().ContainSingle().Which.Reason.Should()
            .Contain("2 unbooked transfers are for").And.Contain("book it by hand");
    }

    [HumansFact]
    public async Task Booking_TextlessLine_UniqueAmount_Books()
    {
        SeedMovements(Movement(description: Textless));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue(result.Message);
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, Arg.Any<Guid?>(), MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_TextlessLine_TwoUnbookedTransfersOfItsAmount_Refuses()
    {
        SeedRows(Row(TransferId, _userId), Row(Guid.NewGuid(), Guid.NewGuid(), OtherAccount));
        SeedMovements(Movement(description: Textless));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("2 unbooked transfers match");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Booking_TextlessLine_WithASecondTextlessLineOfTheAmount_Refuses()
    {
        SeedMovements(Movement(description: Textless), Movement("mov-2", description: Textless));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("2 Sabadell lines could each have paid this transfer");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task TextlessLine_ThatIsAlsoAFilesTotal_IsTakenByNeither()
    {
        // 10 + 20 in one file totals the 30 the default transfer pays: a textless €30 line could
        // be either, so it goes to a human — and neither path takes it.
        var smallFile = Guid.NewGuid();
        var (u1, u2) = (Guid.NewGuid(), Guid.NewGuid());
        SeedRows(
            Row(TransferId, _userId),
            Row(Guid.NewGuid(), u1, 40000011, 10m, fileId: smallFile),
            Row(Guid.NewGuid(), u2, 40000012, 20m, fileId: smallFile));
        SeedContacts(_userId);
        SeedMovements(Movement(description: Textless));
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var booking = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());
        var process = await service.BookSepaFileAsync(smallFile, MovementId, Guid.NewGuid());

        rows.Should().OnlyContain(r => !r.CanBook && r.BatchLine == null);
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("one transfer's line or a whole file's");
        booking.Succeeded.Should().BeFalse();
        booking.Message.Should().Contain("could be a whole file's debit");
        process.Succeeded.Should().BeFalse();
        process.Message.Should().Contain("could be a single transfer's debit");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Sweep_TextlessLine_BooksNothing()
    {
        SeedMovements(Movement(description: Textless));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
    }

    /// <summary>Three days after the file (generated 2026-04-28): outside the default two.</summary>
    private static readonly LocalDate LateLineDate = new(2026, 5, 1);

    [HumansFact]
    public async Task Page_TextlessLine_OutsideTheDefaultTolerance_IsNotPaired_ButIsWhenWidened()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(description: Textless, date: LateLineDate));
        var service = MakeService();

        var (strict, _, strictUnmatched, _) = await service.GetSepaPayoutsAsync(
            ct: TestContext.Current.CancellationToken);
        var (wide, _, wideUnmatched, _) = await service.GetSepaPayoutsAsync(
            toleranceDays: 7, ct: TestContext.Current.CancellationToken);

        strict.Should().ContainSingle().Which.CanBook.Should().BeFalse();
        strictUnmatched.Should().ContainSingle().Which.Reason.Should().Contain("no unbooked transfer is for");
        wide.Should().ContainSingle().Which.CanBook.Should().BeTrue();
        wideUnmatched.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Page_TextMatchedLine_OutsideTheDefaultTolerance_StillPairs()
    {
        // The text ties the line to the account; only a line dated before the file is refused.
        SeedContacts(_userId);
        SeedMovements(Movement(date: LateLineDate));

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(
            ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CanBook.Should().BeTrue();
    }

    [HumansFact]
    public async Task Booking_TextlessLine_OutsideThePostedTolerance_Refuses_AndBooksWhenWidened()
    {
        SeedMovements(Movement(description: Textless, date: LateLineDate));
        var service = MakeService();

        var strict = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());
        await AssertNothingPostedAsync();
        var wide = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid(), toleranceDays: 7);

        strict.Succeeded.Should().BeFalse();
        strict.Message.Should().Contain("does not match this transfer");
        wide.Succeeded.Should().BeTrue(wide.Message);
    }

    [HumansFact]
    public async Task Page_NamedLine_RivalledByAnAmbiguousTextlessLine_IsNotOffered()
    {
        // Two 30s on accounts A and B; one line names A, a textless one could be either. Booking
        // counts the textless line as A's rival and refuses, so the page must not offer it.
        var otherUser = Guid.NewGuid();
        SeedRows(Row(TransferId, _userId), Row(OtherTransferId, otherUser, OtherAccount));
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            new List<HoldedCreditorContact>
            {
                new() { UserId = _userId, HoldedContactId = "c1", SupplierAccountNum = Account, Source = CreditorContactSource.Auto },
                new() { UserId = otherUser, HoldedContactId = "c1", SupplierAccountNum = OtherAccount, Source = CreditorContactSource.Auto },
            });
        SeedMovements(Movement(), Movement("mov-2", description: Textless));
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(
            ct: TestContext.Current.CancellationToken);
        var booking = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        rows.Should().OnlyContain(r => !r.CanBook);
        unmatched.Should().HaveCount(2);
        booking.Succeeded.Should().BeFalse();
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task TextlessLine_WhoseAmountIsTheTotalOfAFilePartlyBookedElsewhere_PairsWithTheTransfer()
    {
        // 10 (booked against its own line) + 20 totals 30, but that file can no longer be a single
        // debit, so the textless €30 line is the default transfer's.
        var partFile = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId),
            Row(Guid.NewGuid(), Guid.NewGuid(), 40000011, 10m, fileId: partFile, bookedAt: FixedNow, movementId: "mov-own"),
            Row(Guid.NewGuid(), Guid.NewGuid(), 40000012, 20m, fileId: partFile));
        SeedContacts(_userId);
        SeedMovements(Movement(description: Textless));
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(
            ct: TestContext.Current.CancellationToken);
        var booking = await service.BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        rows.Single(r => r.TransferId == TransferId).CanBook.Should().BeTrue();
        unmatched.Should().BeEmpty();
        booking.Succeeded.Should().BeTrue(booking.Message);
    }

    [HumansFact]
    public async Task Page_WidenedTolerance_ReadsTheFeedBackFarEnough_ForAFileAtTheWindowEdge()
    {
        // Generated exactly 90 days ago, paid by a textless line 5 days earlier: within days=7, so
        // the feed read has to start that far back too.
        var generatedAt = FixedNow - Duration.FromDays(90);
        var lineDate = new LocalDate(2026, 1, 26);
        SeedRows(Row(TransferId, _userId, generatedAt: generatedAt));
        SeedContacts(_userId);
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? [Movement(description: Textless, date: lineDate)]
                : []);

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(
            toleranceDays: 7, ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().Be(MovementId);
    }

    private static readonly Guid MatchedFileId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherTransferId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>One file of two transfers, 30 and 45, to two members; Sabadell debits each as its
    /// own textless line when <paramref name="bothLines"/>, else only the first.</summary>
    private void SeedMatchedFile(bool bothLines)
    {
        var otherUser = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId, fileId: MatchedFileId),
            Row(OtherTransferId, otherUser, OtherAccount, 45m, fileId: MatchedFileId));
        SeedTransfer(45m, OtherTransferId, otherUser, OtherAccount);
        SeedBinding(otherUser, OtherAccount);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            new List<HoldedCreditorContact>
            {
                new() { UserId = _userId, HoldedContactId = "c1", SupplierAccountNum = Account, Source = CreditorContactSource.Auto },
                new() { UserId = otherUser, HoldedContactId = "c1", SupplierAccountNum = OtherAccount, Source = CreditorContactSource.Auto },
            });
        SeedOwed(30m, (OtherAccount, 45m));
        SeedMovements(bothLines
            ? [Movement(description: Textless), Movement("mov-2", -45m, Textless)]
            : [Movement(description: Textless)]);
    }

    [HumansFact]
    public async Task Page_TextlessLine_RivalledByANotBookableTransfer_IsAmbiguous_AndTheFileIsNotOffered()
    {
        // File A: 30 and 45, both bookable. File B: another 45 whose member has no binding. The
        // €45 line could be either — the page must say so with the same set booking uses, or
        // Book file would book the 30 and then refuse the 45, leaving the file half-booked.
        SeedMatchedFile(bothLines: true);
        var otherUser = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId, fileId: MatchedFileId),
            Row(OtherTransferId, otherUser, OtherAccount, 45m, fileId: MatchedFileId),
            Row(Guid.NewGuid(), Guid.NewGuid(), 40000099, 45m));
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            new List<HoldedCreditorContact>
            {
                new() { UserId = _userId, HoldedContactId = "c1", SupplierAccountNum = Account, Source = CreditorContactSource.Auto },
                new() { UserId = otherUser, HoldedContactId = "c1", SupplierAccountNum = OtherAccount, Source = CreditorContactSource.Auto },
            });
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaFileMatchesAsync(MatchedFileId, Guid.NewGuid());

        rows.Single(r => r.TransferId == TransferId).CanBook.Should().BeTrue();
        rows.Single(r => r.TransferId == OtherTransferId).CanBook.Should().BeFalse();
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("2 unbooked transfers are for");
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("1 of the file's 2 unbooked transfers have no Sabadell line");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task BookFile_WithdrawnTransferInTheFile_GateIsOverTheRest_AndBooksThem()
    {
        // The 45 was cancelled at the bank by hand and withdrawn; Sabadell debits only the 30. The
        // file is "every transfer paired" over what is left, so Book file still offers itself.
        SeedMatchedFile(bothLines: false);
        var otherUser = Guid.NewGuid();
        SeedRows(
            Row(TransferId, _userId, fileId: MatchedFileId),
            Row(OtherTransferId, otherUser, OtherAccount, 45m, fileId: MatchedFileId, withdrawnAt: FixedNow));
        var actor = Guid.NewGuid();
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaFileMatchesAsync(MatchedFileId, actor);

        rows.Single(r => r.TransferId == TransferId).CanBook.Should().BeTrue();
        rows.Single(r => r.TransferId == OtherTransferId).CanBook.Should().BeFalse();
        unmatched.Should().BeEmpty();
        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("Booked all 1 transfer of");
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, actor, MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveSepaTransferBookingAsync(
            OtherTransferId, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Page_WithdrawnTransfer_IsNeverPaired_AndItsLineGoesToThePanel()
    {
        // The line names the account and the amount — exactly the transfer's — and still pairs with
        // nothing: the money was cancelled, so whatever this line is, it is not that payout.
        SeedRows(Row(TransferId, _userId, withdrawnAt: FixedNow));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.IsWithdrawn.Should().BeTrue();
        row.CanBook.Should().BeFalse();
        row.CandidateBankMovementId.Should().BeNull();
        row.NotBookableReason.Should().BeNull();
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("no unbooked transfer of 30.00 EUR on account 40000004");
    }

    [HumansFact]
    public async Task Sweep_SkipsAWithdrawnTransfer_AndPostsNothing()
    {
        SeedRows(Row(TransferId, _userId, withdrawnAt: FixedNow));
        SeedTransfer(30m, withdrawnAt: FixedNow);

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
        await _repo.DidNotReceiveWithAnyArgs().SaveSepaTransferBookingAsync(
            default, default, default, default!, default, default);
    }

    [HumansFact]
    public async Task Booking_WithdrawnTransfer_IsRefused_EvenWithAMatchingLine()
    {
        // Defence in depth: a stale page can still POST the transfer id with a line that fits.
        SeedTransfer(30m, withdrawnAt: FixedNow);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("was withdrawn");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task BookFile_WithEveryLiveTransferBooked_SaysSoOverTheWithdrawnOne()
    {
        SeedRows(
            Row(TransferId, _userId, fileId: MatchedFileId, bookedAt: FixedNow, movementId: MovementId),
            Row(OtherTransferId, Guid.NewGuid(), OtherAccount, 45m, fileId: MatchedFileId, withdrawnAt: FixedNow));

        var result = await MakeService().BookSepaFileMatchesAsync(MatchedFileId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("already booked or withdrawn");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task BookFile_EveryTransferMatched_BooksEachAgainstItsOwnLine()
    {
        SeedMatchedFile(bothLines: true);
        var actor = Guid.NewGuid();

        var result = await MakeService().BookSepaFileMatchesAsync(MatchedFileId, actor);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("Booked all 2 transfers");
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, actor, MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            OtherTransferId, FixedNow, actor, "mov-2", Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BookFile_OneTransferUnmatched_RefusesTheWholeFile_AndPostsNothing()
    {
        SeedMatchedFile(bothLines: false);

        var result = await MakeService().BookSepaFileMatchesAsync(MatchedFileId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("1 of the file's 2 unbooked transfers have no Sabadell line")
            .And.Contain(OtherAccount.ToString());
        await AssertNothingPostedAsync();
    }

    /// <summary>One file of <paramref name="transfers"/> transfers of €30 to as many members, and
    /// <paramref name="lines"/> textless €30 lines dated inside its window. The first member is the
    /// fixture's; <paramref name="unboundSecond"/> leaves the second with no Holded binding.</summary>
    private void SeedEqualAmountSet(int lines, int transfers, bool unboundSecond = false, Guid? secondFileId = null)
    {
        var users = new[] { _userId, Guid.NewGuid(), Guid.NewGuid() };
        var accounts = new[] { Account, OtherAccount, 40000009 };
        var ids = new[] { TransferId, OtherTransferId, Guid.NewGuid() };
        SeedRows(Enumerable.Range(0, transfers)
            .Select(i => Row(ids[i], users[i], accounts[i], fileId: i == 1 && secondFileId is { } f ? f : MatchedFileId))
            .ToArray());
        for (var i = 1; i < transfers; i++)
        {
            SeedTransfer(30m, ids[i], users[i], accounts[i]);
            SeedBinding(users[i], accounts[i]);
        }
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            Enumerable.Range(0, transfers)
                .Where(i => !(unboundSecond && i == 1))
                .Select(i => new HoldedCreditorContact
                {
                    UserId = users[i],
                    HoldedContactId = "c1",
                    SupplierAccountNum = accounts[i],
                    Source = CreditorContactSource.Auto,
                })
                .ToList());
        SeedOwed(30m, (OtherAccount, 30m), (40000009, 30m));
        SeedMovements(Enumerable.Range(1, lines).Select(i => Movement("mov-" + i, description: Textless)).ToArray());
    }

    [HumansFact]
    public async Task Page_TwoTextlessLines_TwoTransfersOfTheAmountInOneFile_PairAsASet_AndTheFileBooks()
    {
        // Which €30 line paid which €30 transfer is unknowable and does not matter: each transfer
        // books its own creditor account, and the lines carry no text. Line by id, transfer by account.
        SeedEqualAmountSet(lines: 2, transfers: 2);
        var actor = Guid.NewGuid();
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaFileMatchesAsync(MatchedFileId, actor);

        unmatched.Should().BeEmpty();
        rows.Should().OnlyContain(r => r.CanBook && !r.CandidateMatchedByText);
        rows.Single(r => r.TransferId == TransferId).CandidateBankMovementId.Should().Be("mov-1");
        rows.Single(r => r.TransferId == OtherTransferId).CandidateBankMovementId.Should().Be("mov-2");
        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("Booked all 2 transfers");
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, actor, "mov-1", Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveSepaTransferBookingAsync(
            OtherTransferId, FixedNow, actor, "mov-2", Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Booking_TextlessSetLine_AgainstTheOtherTransferOfTheSet_Refuses()
    {
        SeedEqualAmountSet(lines: 2, transfers: 2);

        var result = await MakeService().BookSepaTransferAsync(TransferId, "mov-2", Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("does not match this transfer");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task TwoTextlessLines_ThreeTransfersOfTheAmount_StayAmbiguous()
    {
        SeedEqualAmountSet(lines: 2, transfers: 3);
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaTransferAsync(TransferId, "mov-1", Guid.NewGuid());

        rows.Should().OnlyContain(r => !r.CanBook);
        unmatched.Should().HaveCount(2).And.OnlyContain(l => l.Reason.Contains("3 unbooked transfers are for"));
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("3 unbooked transfers match");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task OneTextlessLine_TwoTransfersOfTheAmountInOneFile_StaysAmbiguous()
    {
        SeedEqualAmountSet(lines: 1, transfers: 2);
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaTransferAsync(TransferId, "mov-1", Guid.NewGuid());

        rows.Should().OnlyContain(r => !r.CanBook);
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("2 unbooked transfers are for");
        result.Succeeded.Should().BeFalse();
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task TwoTextlessLines_TwoTransfersOfTheAmountInTwoFiles_StayAmbiguous()
    {
        SeedEqualAmountSet(lines: 2, transfers: 2, secondFileId: Guid.NewGuid());
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaTransferAsync(TransferId, "mov-1", Guid.NewGuid());

        rows.Should().OnlyContain(r => !r.CanBook);
        unmatched.Should().HaveCount(2);
        result.Succeeded.Should().BeFalse();
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Page_TextlessSet_WithANotBookableTransfer_StaysOnThePanel_AndTheFileIsNotOffered()
    {
        SeedEqualAmountSet(lines: 2, transfers: 2, unboundSecond: true);
        var service = MakeService();

        var (rows, _, unmatched, _) = await service.GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await service.BookSepaFileMatchesAsync(MatchedFileId, Guid.NewGuid());

        rows.Should().OnlyContain(r => !r.CanBook);
        rows.Single(r => r.TransferId == OtherTransferId).NotBookableReason.Should().Contain("no Holded contact binding");
        unmatched.Should().HaveCount(2);
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("2 of the file's 2 unbooked transfers have no Sabadell line");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task BookFile_FeedUnreadable_RefusesAndPostsNothing()
    {
        SeedMatchedFile(bothLines: true);
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .ThrowsAsync(new HoldedTransientException("Holded 503"));

        var result = await MakeService().BookSepaFileMatchesAsync(MatchedFileId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("could not be read").And.Contain("Holded 503");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Page_BankFeedUnreadable_StillListsTheRows_AndSaysSoOnce()
    {
        SeedContacts(_userId);
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .ThrowsAsync(new HoldedTransientException("Holded 503"));

        var (rows, unavailable, unmatched, error) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

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

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        unmatched.Should().BeEmpty();
        rows.Single(r => r.TransferId == TransferId).CandidateBankMovementId.Should().Be(MovementId);
        rows.Single(r => r.TransferId == stale).NotBookableReason
            .Should().Contain("settle it in Holded by hand");
    }

    [HumansFact]
    public async Task Booking_LineDatedBeforeTheFileWasGenerated_Refuses()
    {
        // An older unreconciled payment of the same amount on the same creditor account. The money
        // it moved was not this transfer's — the file asking for this one did not exist yet.
        SeedMovements(Movement(date: new LocalDate(2026, 4, 20)));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("does not match this transfer");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Page_LineDatedBeforeTheFileWasGenerated_IsSurfacedForAHuman()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(date: new LocalDate(2026, 4, 20)));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

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
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Page_PartlyReconciledLine_IsNotOfferedAsACandidate()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(status: "partial"));

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
    }

    [HumansFact]
    public async Task Booking_TwoLinesCouldEachHavePaidTheTransfer_Refuses()
    {
        SeedMovements(Movement(), Movement(id: "mov-2"));

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("2 Sabadell lines could each have paid this transfer");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Page_TwoLinesMatchingOneTransfer_AreBothSurfaced_AndNeitherIsOffered()
    {
        SeedContacts(_userId);
        SeedMovements(Movement(), Movement(id: "mov-2"));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().BeNull();
        unmatched.Should().HaveCount(2);
        unmatched.Should().OnlyContain(m => m.Reason.Contains("matches that transfer too"));
    }

    [HumansFact]
    public async Task Sweep_TwoLinesCouldEachHavePaidTheTransfer_BooksNeither()
    {
        SeedMovements(Movement(), Movement(id: "mov-2"));

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
    }

    [HumansTheory]
    [InlineData("partial", false)]
    [InlineData("reconciled", true)]
    public async Task Booking_JournalEntryUnconfirmed_StampsOnlyWhenHoldedThenReadsReconciled(
        string statusAfter, bool stamped)
    {
        // The entry's ref is unconfirmed; the reconcile carries the payment alone either way, and
        // Holded accepting that can still leave the line `partial` with the remainder unmatched.
        SeedOpenDocs(Doc("d1", 10m, 1));
        _client.PostLedgerEntryAsync(
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("unconfirmed:e-1");
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(status: reads++ == 0 ? "pending" : statusAfter)]);

        var result = await MakeService().BookSepaTransferAsync(TransferId, MovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue();
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", MovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 1 && string.Equals(d[0].DocumentId, "pay-d1", StringComparison.Ordinal)),
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

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows.Should().ContainSingle().Which.CandidateBankMovementId.Should().Be(MovementId);
    }

    [HumansFact]
    public async Task Sweep_TransferGeneratedAtTheWindowEdge_BooksALineDatedInTheSlackBeforeIt()
    {
        SeedWindowEdgeTransfer();

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).SaveSepaTransferBookingAsync(
            TransferId, FixedNow, null, MovementId, Arg.Any<Instant?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private void SeedWindowEdgeTransfer()
    {
        var generatedAt = FixedNow - Duration.FromDays(90);
        var lineDate = new LocalDate(2026, 1, 29);
        SeedRows(Row(TransferId, _userId, generatedAt: generatedAt));
        // The stub honours the requested window, as the live feed does.
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci => ci.ArgAt<LocalDate>(1) <= lineDate
                ? [Movement(date: lineDate)]
                : []);
    }

    private static readonly Guid BatchFileId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string BatchMovementId = "mov-batch";

    /// <summary>The Friday test: 10, 20, 30 and 40 to four members, debited by Sabadell as one
    /// €100 line whose text names no creditor account.</summary>
    private static readonly (Guid Id, Guid User, int Account, decimal Amount)[] Batch =
    [
        (Guid.Parse("a0000000-0000-0000-0000-000000000001"), Guid.NewGuid(), 40000011, 10m),
        (Guid.Parse("a0000000-0000-0000-0000-000000000002"), Guid.NewGuid(), 40000012, 20m),
        (Guid.Parse("a0000000-0000-0000-0000-000000000003"), Guid.NewGuid(), 40000013, 30m),
        (Guid.Parse("a0000000-0000-0000-0000-000000000004"), Guid.NewGuid(), 40000014, 40m),
    ];

    /// <summary>Four transfers the bank debited as one €100 line. Each creditor is its own Holded
    /// contact; with <paramref name="withDocs"/> each has one open document of its amount, otherwise
    /// every transfer books as a journal entry.</summary>
    private void SeedBatch(
        string description = "TRANSFERENCIAS SEPA", Guid? bookedFirst = null, bool withDocs = false)
    {
        SeedRows(Batch.Select(t => Row(
                t.Id, t.User, t.Account, t.Amount, fileId: BatchFileId,
                bookedAt: t.Id == bookedFirst ? FixedNow : null,
                movementId: t.Id == bookedFirst ? BatchMovementId : null,
                contactId: "c-" + t.Account))
            .ToArray());
        foreach (var t in Batch)
        {
            SeedTransfer(t.Amount, t.Id, t.User, t.Account, contactId: "c-" + t.Account);
            SeedBinding(t.User, t.Account, contactId: "c-" + t.Account);
        }
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            Batch.Select(t => new HoldedCreditorContact
            {
                UserId = t.User,
                HoldedContactId = "c-" + t.Account,
                SupplierAccountNum = t.Account,
                Source = CreditorContactSource.Auto,
            }).ToList());
        if (withDocs)
            SeedOpenDocs(Batch.Select(t => Doc("d-" + t.Account, t.Amount, 1, "c-" + t.Account)).ToArray());
        SeedOwed(0m, Batch.Select(t => (t.Account, t.Amount)).ToArray());
        SeedMovements(Movement(BatchMovementId, -100m, description));
        _client.PostLedgerEntryAsync(
                Arg.Any<LocalDate>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => "e-" + ci.ArgAt<int>(1));
    }

    [HumansFact]
    public async Task Page_LineForTheWholeFile_IsOfferedOnThatFile_NotInThePanel()
    {
        SeedBatch();

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        unmatched.Should().BeEmpty();
        rows.Should().HaveCount(4).And.OnlyContain(r => r.BatchLine != null && !r.CanBook);
        var line = rows[0].BatchLine!;
        line.MovementId.Should().Be(BatchMovementId);
        line.Amount.Should().Be(-100m);
        line.QuotesFileReference.Should().BeFalse();
        line.NotProcessableReason.Should().BeNull();
    }

    [HumansFact]
    public async Task Page_LineQuotingTheFileMsgId_SaysSo()
    {
        SeedBatch("TRANSF SEPA M" + BatchFileId.ToString("N").ToUpperInvariant());

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows[0].BatchLine!.QuotesFileReference.Should().BeTrue();
    }

    [HumansFact]
    public async Task Page_TwoFilesWithTheLinesTotal_OffersNeither_AndSurfacesTheLine()
    {
        SeedBatch();
        var otherFile = Guid.NewGuid();
        var rows = Batch.Select(t => Row(t.Id, t.User, t.Account, t.Amount, fileId: BatchFileId))
            .Concat([
                Row(Guid.NewGuid(), _userId, Account, 50m, fileId: otherFile),
                Row(Guid.NewGuid(), _userId, Account, 50m, fileId: otherFile),
            ])
            .ToArray();
        SeedRows(rows);

        var (result, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        result.Should().OnlyContain(r => r.BatchLine == null);
        unmatched.Should().ContainSingle().Which.Reason.Should().Contain("2 payout files total 100.00 EUR");
    }

    [HumansFact]
    public async Task TwoLinesWithTheFilesTotal_NeitherIsOffered_AndProcessRefuses()
    {
        SeedBatch();
        SeedMovements(
            Movement(BatchMovementId, -100m, "TRANSFERENCIAS SEPA"),
            Movement("mov-batch-2", -100m, "TRANSFERENCIAS SEPA"));

        var (rows, _, unmatched, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, Guid.NewGuid());

        rows.Should().OnlyContain(r => r.BatchLine == null);
        unmatched.Should().HaveCount(2)
            .And.OnlyContain(m => m.Reason.Contains("matches that file's total too"));
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("2 Sabadell lines could each be this file's debit");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task ATransferWithALineOfItsOwn_TheFileIsNotOffered_AndProcessRefuses()
    {
        // The bank debited per transfer after all; a €100 line on the same feed is another payment.
        SeedBatch();
        SeedMovements(
            Movement(BatchMovementId, -100m, "TRANSFERENCIAS SEPA"),
            Movement("mov-own", -10m, "TRANSF 40000011 - NCA - ANA RUIZ"));

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);
        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, Guid.NewGuid());

        rows.Should().OnlyContain(r => r.BatchLine == null);
        rows.Single(r => r.TransferId == Batch[0].Id).CandidateBankMovementId.Should().Be("mov-own");
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("has a Sabadell line of its own");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Page_FileWithAnUnboundTransfer_SaysWhyItCannotBeProcessed()
    {
        SeedBatch();
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            Batch.Skip(1).Select(t => new HoldedCreditorContact
            {
                UserId = t.User,
                HoldedContactId = "c-" + t.Account,
                SupplierAccountNum = t.Account,
                Source = CreditorContactSource.Auto,
            }).ToList());

        var (rows, _, _, _) = await MakeService().GetSepaPayoutsAsync(ct: TestContext.Current.CancellationToken);

        rows[0].BatchLine!.NotProcessableReason.Should().Contain("40000011").And.Contain("no Holded contact binding");
    }

    [HumansFact]
    public async Task Process_BooksEveryTransferAgainstTheLine_AndReconcilesItOnce()
    {
        SeedBatch(withDocs: true);
        var actor = Guid.NewGuid();

        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, actor);

        result.Succeeded.Should().BeTrue(result.Message);
        foreach (var t in Batch)
        {
            await _client.Received(1).PayPurchaseDocumentAsync(
                "d-" + t.Account, t.Amount, "treasury-1", LineDate, "SEPA payout E" + t.Id.ToString("N"),
                Arg.Any<CancellationToken>());
            await _repo.Received(1).SaveSepaTransferBookingAsync(
                t.Id, FixedNow, actor, BatchMovementId, null, "pay-d-" + t.Account, Arg.Any<CancellationToken>());
            await _repo.Received(1).MarkSepaTransferReconciledAsync(
                t.Id, FixedNow, Arg.Any<CancellationToken>());
        }
        await _client.Received(1).ReconcileBankMovementAsync(
            "treasury-1", BatchMovementId,
            Arg.Is<IReadOnlyList<HoldedReconcileDocumentRef>>(d =>
                d.Count == 4
                && d.Select(x => x.DocumentId).SequenceEqual(Batch.Select(t => "pay-d-" + t.Account))
                && d.All(x => string.Equals(x.DocumentType, HoldedReconcileDocumentType.Payment, StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Process_PaidByJournalEntriesAlone_BooksButCannotReconcile_AndSaysSo()
    {
        // Holded cannot link a bank line to a ledger entry, so there is nothing to reconcile with:
        // the file is booked, the line is a human's to reconcile, and every row says so.
        SeedBatch();

        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("RECONCILE PENDING").And.Contain("by hand");
        await _client.DidNotReceiveWithAnyArgs().ReconcileBankMovementAsync(
            default!, default!, default!);
        await _repo.DidNotReceiveWithAnyArgs().MarkSepaTransferReconciledAsync(default, default);
    }

    [HumansFact]
    public async Task Process_LineIsNotTheFilesTotal_RefusesAndPostsNothing()
    {
        SeedBatch();
        SeedMovements(Movement(BatchMovementId, -90m, "TRANSFERENCIAS SEPA"));

        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("not the 100.00 EUR this file totals");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Process_OneTransferUnbound_RefusesBeforePostingAnything()
    {
        SeedBatch();
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            Batch.Take(3).Select(t => new HoldedCreditorContact
            {
                UserId = t.User,
                HoldedContactId = "c-" + t.Account,
                SupplierAccountNum = t.Account,
                Source = CreditorContactSource.Auto,
            }).ToList());

        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("40000014");
        await AssertNothingPostedAsync();
    }

    [HumansFact]
    public async Task Process_AfterAPartialRun_BooksOnlyTheRest_AndStampsOnlyWhenHoldedSaysReconciled()
    {
        SeedBatch(bookedFirst: Batch[0].Id, withDocs: true);
        var reads = 0;
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
            .Returns(_ =>
                [Movement(BatchMovementId, -100m, "TRANSFERENCIAS SEPA", reads++ == 0 ? "pending" : "reconciled")]);

        var result = await MakeService().BookSepaFileAsync(BatchFileId, BatchMovementId, Guid.NewGuid());

        result.Succeeded.Should().BeTrue(result.Message);
        await _repo.DidNotReceive().SaveSepaTransferBookingAsync(
            Batch[0].Id, Arg.Any<Instant>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<Instant?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _repo.Received(3).SaveSepaTransferBookingAsync(
            Arg.Any<Guid>(), FixedNow, Arg.Any<Guid?>(), BatchMovementId, null, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // The earlier run's transfer is part of the same line, so it is stamped too.
        await _repo.Received(1).MarkSepaTransferReconciledAsync(
            Batch[0].Id, FixedNow, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sweep_NeverBooksALineForAWholeFile()
    {
        SeedBatch();

        await MakeService().RunAsync(TestContext.Current.CancellationToken);

        await AssertNothingPostedAsync();
    }

    private async Task AssertNothingPostedAsync()
    {
        await _client.DidNotReceiveWithAnyArgs().PayPurchaseDocumentAsync(
            default!, default, default, default, default!);
        await _client.DidNotReceiveWithAnyArgs().PostLedgerEntryAsync(
            default, default, default, default, default!);
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
        decimal amount, Guid? id = null, Guid? userId = null, int account = Account,
        string contactId = "c1", string? paymentRefs = null, Instant? withdrawnAt = null) =>
        _repo.GetSepaTransferAsync(id ?? TransferId, Arg.Any<CancellationToken>()).Returns(
            new SepaPayoutTransfer
            {
                Id = id ?? TransferId,
                FileId = Guid.NewGuid(),
                UserId = userId ?? _userId,
                SupplierAccountNum = account,
                HoldedContactId = contactId,
                CreditorName = "Ana Ruiz",
                Iban = AnaIban,
                IbanMasked = AnaIbanMasked,
                Amount = amount,
                HoldedPaymentRefs = paymentRefs,
                WithdrawnAt = withdrawnAt,
            });

    private void SeedBinding(Guid userId, int account = Account, string contactId = "c1") =>
        _repo.GetCreditorContactByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new HoldedCreditorContact
            {
                UserId = userId,
                HoldedContactId = contactId,
                SupplierAccountNum = account,
                Source = CreditorContactSource.Auto,
            });

    private void SeedRows(params SepaPayoutTransferRow[] rows) =>
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(rows.ToList());

    private static SepaPayoutTransferRow Row(
        Guid id, Guid userId, int account = Account, decimal amount = 30m,
        Instant? bookedAt = null, string? movementId = null, Instant? reconciledAt = null,
        Instant? generatedAt = null, Guid? fileId = null, string? paymentRefs = null,
        string contactId = "c1", Instant? withdrawnAt = null) =>
        new(id, fileId ?? Guid.NewGuid(), "payout.xml", generatedAt ?? FixedNow - Duration.FromDays(3), Guid.NewGuid(),
            userId, account, contactId, "Ana Ruiz", AnaIbanMasked, amount, bookedAt, null, movementId,
            reconciledAt, paymentRefs, null, null, WithdrawnAt: withdrawnAt);

    private static HoldedBankMovementDto Movement(
        string id = MovementId, decimal amount = -30m, string? description = Remittance,
        string status = "pending", LocalDate? date = null) =>
        new()
        {
            Id = id,
            Date = date ?? LineDate,
            Amount = amount,
            Description = description,
            Status = status,
        };

    private void SeedMovements(params HoldedBankMovementDto[] movements) =>
        _client.ListBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<string>?>())
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
        string id, decimal pending, int day, string contactId = "c1") =>
        new()
        {
            Id = id,
            DocNumber = id.ToUpperInvariant(),
            ContactId = contactId,
            ContactName = "Ana Ruiz",
            Date = Instant.FromUtc(2026, 4, 1, 0, 0) + Duration.FromDays(day - 1),
            Subtotal = pending,
            Tax = 0m,
            Total = pending,
            PaymentsPending = pending,
            IsDraft = false,
        };

    private void SeedOpenDocs(params HoldedPurchaseDocListItemDto[] docs) =>
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(docs.ToList());
}
