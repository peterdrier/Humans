using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services;
using Humans.Holded.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Expenses.Tests.Services;

/// <summary>
/// The lifecycle and matching-run behaviour of the vendor commitment registry
/// (nobodies-collective/Humans#1030), over a real in-memory repository so the derived status and
/// the review-queue rows are asserted as they actually persist.
/// </summary>
public sealed class VendorCommitmentServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 1, 9, 0);
    private static readonly LocalDate PaidOn = new(2026, 5, 30);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IHoldedClient _holded = Substitute.For<IHoldedClient>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IFileStorage _files = Substitute.For<IFileStorage>();
    private readonly IVendorCommitmentRepository _repo;
    private readonly VendorCommitmentService _sut;

    public VendorCommitmentServiceTests()
    {
        var options = new DbContextOptionsBuilder<ExpensesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _repo = new VendorCommitmentRepository(new TestDbContextFactory<ExpensesDbContext>(options));
        _holded.IsConfigured.Returns(true);
        _holded.ListDraftPurchaseIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal));
        _sut = new VendorCommitmentService(
            _repo, _files, _holded, _audit, new FakeClock(Now),
            NullLogger<VendorCommitmentService>.Instance);
    }

    private async Task<Guid> RecordAsync(decimal amount = 1_000m, string vendor = "TOI TOI")
    {
        var (result, id) = await _sut.CreateAsync(
            vendor, amount, "Sanitary units", null, Guid.NewGuid(), null, Ct);
        result.Succeeded.Should().BeTrue();
        return id!.Value;
    }

    [HumansFact]
    public async Task CreateAsync_RecordsAnOpenCommitment_BeforeAnyPayment()
    {
        var id = await RecordAsync(69_398.34m);

        var commitment = await _sut.GetAsync(id, Ct);
        commitment.Should().NotBeNull();
        commitment!.Status.Should().Be(VendorCommitmentStatus.Open);
        commitment.TotalPaid.Should().Be(0m);
        commitment.ExpectedAmount.Should().Be(69_398.34m);
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task CreateAsync_RejectsANonPositiveAmount(decimal amount)
    {
        var (result, id) = await _sut.CreateAsync(
            "Repsol", amount, "Fuel", null, Guid.NewGuid(), null, Ct);

        result.Succeeded.Should().BeFalse();
        id.Should().BeNull();
    }

    [HumansFact]
    public async Task CreateAsync_RejectsABlankVendor()
    {
        var (result, _) = await _sut.CreateAsync(
            "  ", 100m, "Fuel", null, Guid.NewGuid(), null, Ct);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task RecordPaymentAsync_PartOfTheAmount_MovesToPartiallyPaid()
    {
        var id = await RecordAsync(1_000m);

        var result = await _sut.RecordPaymentAsync(id, 400m, PaidOn, "TR-1", Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeTrue();
        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.Status.Should().Be(VendorCommitmentStatus.PartiallyPaid);
        commitment.TotalPaid.Should().Be(400m);
    }

    [HumansFact]
    public async Task RecordPaymentAsync_ReachingTheAmount_MovesToPaid()
    {
        var id = await RecordAsync(1_000m);
        await _sut.RecordPaymentAsync(id, 400m, PaidOn, null, Guid.NewGuid(), Ct);
        await _sut.RecordPaymentAsync(id, 600m, PaidOn, null, Guid.NewGuid(), Ct);

        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.Status.Should().Be(VendorCommitmentStatus.Paid);
        commitment.TotalPaid.Should().Be(1_000m);
    }

    [HumansFact]
    public async Task RecordPaymentAsync_RejectsANonPositiveAmount()
    {
        var id = await RecordAsync();

        var result = await _sut.RecordPaymentAsync(id, 0m, PaidOn, null, Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(id, Ct))!.Payments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task RecordPaymentAsync_IsRefusedOnAClosedCommitment()
    {
        var id = await RecordAsync();
        (await _sut.CloseAsync(id, Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();

        var result = await _sut.RecordPaymentAsync(id, 10m, PaidOn, null, Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeFalse();
    }

    [HumansFact]
    public async Task CloseAsync_IsRefusedWhilePaidButNotInvoiced()
    {
        var id = await RecordAsync(1_000m);
        await _sut.RecordPaymentAsync(id, 1_000m, PaidOn, null, Guid.NewGuid(), Ct);

        var result = await _sut.CloseAsync(id, Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(id, Ct))!.Status.Should().Be(VendorCommitmentStatus.Paid);
    }

    [HumansFact]
    public async Task ListPaidAwaitingInvoiceAsync_ListsExactlyThePaidButUnmatched()
    {
        var paidNoInvoice = await RecordAsync(1_000m, "TOI TOI");
        await _sut.RecordPaymentAsync(paidNoInvoice, 1_000m, PaidOn, null, Guid.NewGuid(), Ct);

        var unpaid = await RecordAsync(500m, "Repsol");

        var invoiced = await RecordAsync(250m, "Covey");
        await _sut.RecordPaymentAsync(invoiced, 250m, PaidOn, null, Guid.NewGuid(), Ct);
        _holded.ListPurchaseDocumentsAsync(Ct).Returns([ListItem("doc-1", 250m, "Covey")]);
        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        var awaiting = await _sut.ListPaidAwaitingInvoiceAsync(Ct);

        awaiting.Select(c => c.Id).Should().BeEquivalentTo([paidNoInvoice]);
        awaiting.Select(c => c.Id).Should().NotContain(unpaid).And.NotContain(invoiced);
    }

    [HumansFact]
    public async Task RunMatchingAsync_LinksTheSingleExactFit_AndMarksItInvoiced()
    {
        var id = await RecordAsync(69_398.34m, "TOI TOI");
        await _sut.RecordPaymentAsync(id, 69_398.34m, PaidOn, null, Guid.NewGuid(), Ct);
        _holded.ListPurchaseDocumentsAsync(Ct)
            .Returns([ListItem("doc-1", 69_398.34m, "TOI TOI, S.L.")]);

        var (result, run) = await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeTrue();
        run!.Linked.Should().Be(1);
        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.Status.Should().Be(VendorCommitmentStatus.Invoiced);
        commitment.MatchedHoldedDocId.Should().Be("doc-1");
        commitment.PendingCandidates.Should().BeEmpty();
    }

    // AC4 end to end: a second document for an invoiced commitment queues rather than overwriting.
    [HumansFact]
    public async Task RunMatchingAsync_SecondDocumentForAnInvoicedCommitment_QueuesADuplicate()
    {
        var id = await RecordAsync(69_398.34m, "TOI TOI");
        _holded.ListPurchaseDocumentsAsync(Ct)
            .Returns([ListItem("doc-1", 69_398.34m, "TOI TOI")]);
        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        _holded.ListPurchaseDocumentsAsync(Ct).Returns(
        [
            ListItem("doc-1", 69_398.34m, "TOI TOI"),
            ListItem("doc-2", 69_398.34m, "TOI TOI"),
        ]);
        var (_, run) = await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        run!.Duplicates.Should().Be(1);
        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.MatchedHoldedDocId.Should().Be("doc-1");
        commitment.PendingCandidates.Should().ContainSingle()
            .Which.Kind.Should().Be(VendorCommitmentMatchKind.Duplicate);
    }

    // AC6 end to end: nothing gets linked while the tie is unresolved.
    [HumansFact]
    public async Task RunMatchingAsync_TwoEqualFits_QueueAmbiguous_AndLinkNothing()
    {
        var id = await RecordAsync(4_024.30m, "Talleres Fandos");
        _holded.ListPurchaseDocumentsAsync(Ct).Returns(
        [
            ListItem("doc-1", 4_024.30m, "Talleres Fandos"),
            ListItem("doc-2", 4_024.30m, "Talleres Fandos SL"),
        ]);

        var (_, run) = await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        run!.Linked.Should().Be(0);
        run.Ambiguous.Should().Be(2);
        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.MatchedHoldedDocId.Should().BeNull();
        commitment.Status.Should().Be(VendorCommitmentStatus.Open);
        commitment.PendingCandidates.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task ResolveCandidateAsync_Accept_LinksTheChosenDocument()
    {
        var id = await RecordAsync(4_024.30m, "Talleres Fandos");
        _holded.ListPurchaseDocumentsAsync(Ct).Returns(
        [
            ListItem("doc-1", 4_024.30m, "Talleres Fandos"),
            ListItem("doc-2", 4_024.30m, "Talleres Fandos SL"),
        ]);
        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);
        var chosen = (await _sut.GetAsync(id, Ct))!.PendingCandidates
            .First(c => string.Equals(c.HoldedDocId, "doc-2", StringComparison.Ordinal));

        var result = await _sut.ResolveCandidateAsync(chosen.Id, accepted: true, Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeTrue();
        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.MatchedHoldedDocId.Should().Be("doc-2");
        commitment.Status.Should().Be(VendorCommitmentStatus.Invoiced);
    }

    [HumansFact]
    public async Task ResolveCandidateAsync_Dismiss_LeavesTheCommitmentUnmatched()
    {
        var id = await RecordAsync(4_024.30m, "Talleres Fandos");
        _holded.ListPurchaseDocumentsAsync(Ct).Returns(
        [
            ListItem("doc-1", 4_024.30m, "Talleres Fandos"),
            ListItem("doc-2", 4_024.30m, "Talleres Fandos SL"),
        ]);
        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);
        var first = (await _sut.GetAsync(id, Ct))!.PendingCandidates[0];

        await _sut.ResolveCandidateAsync(first.Id, accepted: false, Guid.NewGuid(), Ct);

        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.MatchedHoldedDocId.Should().BeNull();
        commitment.PendingCandidates.Should().ContainSingle();
    }

    // Re-running the matcher must not resurrect a decision a human already made.
    [HumansFact]
    public async Task RunMatchingAsync_DoesNotReopenADismissedCandidate()
    {
        var id = await RecordAsync(4_024.30m, "Talleres Fandos");
        _holded.ListPurchaseDocumentsAsync(Ct).Returns(
        [
            ListItem("doc-1", 4_024.30m, "Talleres Fandos"),
            ListItem("doc-2", 4_024.30m, "Talleres Fandos SL"),
        ]);
        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);
        var dismissed = (await _sut.GetAsync(id, Ct))!.PendingCandidates[0];
        await _sut.ResolveCandidateAsync(dismissed.Id, accepted: false, Guid.NewGuid(), Ct);

        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.PendingCandidates.Select(c => c.HoldedDocId)
            .Should().NotContain(dismissed.HoldedDocId);
    }

    [HumansFact]
    public async Task RunMatchingAsync_WithoutHoldedConfigured_DoesNothing()
    {
        _holded.IsConfigured.Returns(false);
        var id = await RecordAsync();

        var (result, run) = await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeFalse();
        run.Should().BeNull();
        (await _sut.GetAsync(id, Ct))!.MatchedHoldedDocId.Should().BeNull();
    }

    [HumansFact]
    public async Task RunMatchingAsync_NeverGivesOneDocumentToTwoCommitments()
    {
        var first = await RecordAsync(500m, "Repsol");
        var second = await RecordAsync(500m, "Repsol");
        _holded.ListPurchaseDocumentsAsync(Ct).Returns([ListItem("doc-1", 500m, "Repsol")]);

        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        var linked = new[] { first, second }
            .Select(id => _sut.GetAsync(id, Ct).GetAwaiter().GetResult()!.MatchedHoldedDocId)
            .Count(docId => docId is not null);
        linked.Should().Be(1);
    }

    // A draft books nothing in Holded, so linking one would mark the commitment Invoiced and drop
    // it off the liability list with no real invoice behind it.
    [HumansFact]
    public async Task RunMatchingAsync_NeverLinksADraftPurchaseDocument()
    {
        var id = await RecordAsync(1_000m, "Repsol");
        await _sut.RecordPaymentAsync(id, 1_000m, PaidOn, null, Guid.NewGuid(), Ct);
        _holded.ListPurchaseDocumentsAsync(Ct).Returns([ListItem("draft-1", 1_000m, "Repsol")]);
        _holded.ListDraftPurchaseIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(StringComparer.Ordinal) { "draft-1" });

        var (_, run) = await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);

        run!.Linked.Should().Be(0);
        var commitment = await _sut.GetAsync(id, Ct);
        commitment!.MatchedHoldedDocId.Should().BeNull();
        commitment.PendingCandidates.Should().BeEmpty();
        commitment.IsPaidAwaitingInvoice.Should().BeTrue();
    }

    // A quote that fails to store must not make a committed row look like it was never written —
    // the operator would record the same liability again.
    [HumansFact]
    public async Task CreateAsync_WhenStoringTheQuoteFails_KeepsTheCommitmentAndReturnsItsId()
    {
        _files.SaveAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("volume not mounted"));
        await using var content = new MemoryStream([1, 2, 3]);

        var (result, id) = await _sut.CreateAsync(
            "Repsol", 500m, "Fuel", null, Guid.NewGuid(),
            new ExpenseFileUpload("quote.pdf", "application/pdf", content), Ct);

        result.Succeeded.Should().BeFalse();
        id.Should().NotBeNull();
        var commitment = await _sut.GetAsync(id!.Value, Ct);
        commitment.Should().NotBeNull();
        commitment!.ExpectedAmount.Should().Be(500m);
        commitment.QuoteFileName.Should().BeNull();
    }

    /// <summary>
    /// Two commitments to the same vendor for the same amount, two documents that fit both. Every
    /// document is Ambiguous on both commitments, and a Review decision claims nothing — so the
    /// review queue, unlike the matching run, can offer one document to two operators in turn.
    /// </summary>
    private async Task<(Guid First, Guid Second)> TwoCommitmentsSharingCandidatesAsync()
    {
        var first = await RecordAsync(500m, "Repsol");
        var second = await RecordAsync(500m, "Repsol");
        _holded.ListPurchaseDocumentsAsync(Ct).Returns(
        [
            ListItem("doc-1", 500m, "Repsol"),
            ListItem("doc-2", 500m, "Repsol"),
        ]);
        await _sut.RunMatchingAsync(Guid.NewGuid(), Ct);
        return (first, second);
    }

    private async Task<Guid> PendingCandidateIdAsync(Guid commitmentId, string holdedDocId) =>
        (await _sut.GetAsync(commitmentId, Ct))!.PendingCandidates
            .First(c => string.Equals(c.HoldedDocId, holdedDocId, StringComparison.Ordinal)).Id;

    [HumansFact]
    public async Task ResolveCandidateAsync_CannotAcceptADocumentAnotherCommitmentAlreadyCarries()
    {
        var (first, second) = await TwoCommitmentsSharingCandidatesAsync();
        var onSecond = await PendingCandidateIdAsync(second, "doc-1");
        var onFirst = await PendingCandidateIdAsync(first, "doc-1");
        (await _sut.ResolveCandidateAsync(onFirst, accepted: true, Guid.NewGuid(), Ct))
            .Succeeded.Should().BeTrue();

        var result = await _sut.ResolveCandidateAsync(onSecond, accepted: true, Guid.NewGuid(), Ct);

        result.Succeeded.Should().BeFalse();
        (await _sut.GetAsync(second, Ct))!.MatchedHoldedDocId.Should().BeNull();
        (await _sut.GetAsync(first, Ct))!.MatchedHoldedDocId.Should().Be("doc-1");
    }

    [HumansFact]
    public async Task ResolveCandidateAsync_Accept_DropsTheDocumentFromEveryOtherReviewQueue()
    {
        var (first, second) = await TwoCommitmentsSharingCandidatesAsync();

        await _sut.ResolveCandidateAsync(
            await PendingCandidateIdAsync(first, "doc-1"), accepted: true, Guid.NewGuid(), Ct);

        var pending = (await _sut.GetAsync(second, Ct))!.PendingCandidates;
        pending.Select(c => c.HoldedDocId).Should().BeEquivalentTo(["doc-2"]);
    }

    private static HoldedPurchaseDocListItemDto ListItem(string id, decimal total, string contact) =>
        new()
        {
            Id = id,
            DocNumber = $"PC-{id}",
            ContactName = contact,
            Date = Instant.FromUtc(2026, 6, 1, 0, 0),
            Subtotal = total,
            Tax = 0m,
            Total = total,
        };
}
