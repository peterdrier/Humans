using Humans.Holded.Contracts;
using NodaTime;
using NSubstitute;

namespace Humans.Finance.Tests;

/// <summary>Holded's data as a test seeds it, in the shape Holded returns it. Finance never sees this:
/// <see cref="TestHoldedSync"/> serves it through the <see cref="IHoldedService"/> syncs and mirrors.</summary>
public interface IHoldedFeed
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HoldedBankMovementDto>> ListBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default,
        IReadOnlySet<string>? skipUnreadableDateIds = null);
}

/// <summary>Answers Holded's sync-then-mirror reads from an <see cref="IHoldedFeed"/>, so a test seeds Holded's
/// data once. A sync reads the feed (and throws what it throws); the mirror then serves what the last sync read.</summary>
internal static class TestHoldedSync
{
    public static void Wire(IHoldedService holded, IHoldedFeed feed)
    {
        holded.IsConfigured.Returns(_ => feed.IsConfigured);

        IReadOnlyList<HoldedBankMovementDto> movements = [];
        var synced = false;
        holded.SyncBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                movements = await feed.ListBankMovementsAsync(
                    ci.ArgAt<string>(0), ci.ArgAt<LocalDate>(1), ci.ArgAt<LocalDate>(2), ci.ArgAt<CancellationToken>(4),
                    ci.ArgAt<IReadOnlySet<string>?>(3));
                synced = true;
                return true;
            });
        holded.GetBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(async ci => new HoldedBankMovementMirror(Instant.FromUtc(2026, 1, 1, 0, 0), synced
                ? movements
                // A page read with no sync before it: what the nightly sync would have stored.
                : await feed.ListBankMovementsAsync(
                    ci.ArgAt<string>(0), ci.ArgAt<LocalDate>(1), ci.ArgAt<LocalDate>(2), CancellationToken.None,
                    null)));

        holded.SyncAccountLedgerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        // The chart's balance rides in as an opening line, so the lines sum to what the chart says is owed.
        holded.GetLedgerLinesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            var account = ci.ArgAt<int>(0);
            var entries = (await feed.ListLedgerEntriesAsync(
                    LocalDate.MinIsoValue, LocalDate.MaxIsoValue, account, CancellationToken.None))
                .Select(l => new HoldedLedgerLineInfo(
                    l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit))
                .ToList();
            var balance = (await feed.ListAccountingAccountsAsync(CancellationToken.None))
                .Where(a => a.Number == account).Select(a => a.Balance).DefaultIfEmpty(0m).First();
            var opening = -balance + entries.Where(l => l.AccountNum == account).Sum(l => l.Debit - l.Credit);
            return (IReadOnlyList<HoldedLedgerLineInfo>)entries
                .Prepend(new HoldedLedgerLineInfo(0, 0, account, Instant.FromUtc(2026, 1, 1, 0, 0), "opening", null,
                    opening < 0m ? -opening : 0m, opening > 0m ? opening : 0m))
                .ToList();
        });

        holded.SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(true);
        holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
            TestPurchaseMirror.Of(await feed.ListPurchaseDocumentsAsync(CancellationToken.None)));
    }
}
