using Humans.Holded.Contracts;
using NodaTime;
using NSubstitute;

namespace Humans.Finance.Tests;

/// <summary>Holded's data as a test seeds it, in the shape Holded's connector reads it. The connector is internal to
/// Holded, so this is a stand-in for what Holded's syncs would read.</summary>
public interface IHoldedFeed
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<HoldedBankMovementDto>> ListBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default,
        IReadOnlySet<string>? skipUnreadableDateIds = null);

    Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(CancellationToken ct = default);
}

/// <summary>Stands in for Holded's sync-then-mirror reads by answering them from the <see cref="IHoldedFeed"/>
/// fake, so a test seeds Holded's data once. A sync reads the feed (and throws what it
/// throws); the mirror then serves what the last sync read.</summary>
internal static class TestHoldedSync
{
    public static void Wire(IHoldedService holded, IHoldedFeed client)
    {
        holded.IsConfigured.Returns(_ => client.IsConfigured);

        IReadOnlyList<HoldedBankMovementDto> feed = [];
        var synced = false;
        holded.SyncBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                feed = await client.ListBankMovementsAsync(
                    ci.ArgAt<string>(0), ci.ArgAt<LocalDate>(1), ci.ArgAt<LocalDate>(2), ci.ArgAt<CancellationToken>(4),
                    ci.ArgAt<IReadOnlySet<string>?>(3));
                synced = true;
                return true;
            });
        holded.GetBankMovementsAsync(
                Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(async ci => new HoldedBankMovementMirror(Instant.FromUtc(2026, 1, 1, 0, 0), synced
                ? feed
                // A page read with no sync before it: what the nightly sync would have stored.
                : await client.ListBankMovementsAsync(
                    ci.ArgAt<string>(0), ci.ArgAt<LocalDate>(1), ci.ArgAt<LocalDate>(2), CancellationToken.None,
                    null)));

        holded.SyncAccountLedgerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        // The chart's balance rides in as an opening line, so the lines sum to what the chart says is owed.
        holded.GetLedgerLinesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            var account = ci.ArgAt<int>(0);
            var entries = (await client.ListLedgerEntriesAsync(
                    LocalDate.MinIsoValue, LocalDate.MaxIsoValue, account, CancellationToken.None))
                .Select(l => new HoldedLedgerLineInfo(
                    l.EntryNumber, l.Line, l.AccountNum, l.Date, l.Type, l.Description, l.Debit, l.Credit))
                .ToList();
            var balance = (await client.ListAccountingAccountsAsync(CancellationToken.None))
                .Where(a => a.Number == account).Select(a => a.Balance).DefaultIfEmpty(0m).First();
            var opening = -balance + entries.Where(l => l.AccountNum == account).Sum(l => l.Debit - l.Credit);
            return (IReadOnlyList<HoldedLedgerLineInfo>)entries
                .Prepend(new HoldedLedgerLineInfo(0, 0, account, Instant.FromUtc(2026, 1, 1, 0, 0), "opening", null,
                    opening < 0m ? -opening : 0m, opening > 0m ? opening : 0m))
                .ToList();
        });

        holded.SyncPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(true);
        holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
            TestPurchaseMirror.Of(await client.ListPurchaseDocumentsAsync(CancellationToken.None)));
    }
}
