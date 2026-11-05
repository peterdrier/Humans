using Humans.Pleo.Contracts;
using NodaTime;

namespace Humans.Finance.Models;

/// <summary>Where one Pleo spend entry stands against Holded 57200003. <see cref="Pending"/>: this process posted it
/// and the ledger does not show it yet; it counts as booked and is never posted again.</summary>
internal enum PleoSpendState
{
    Booked,
    Pending,
    Ready,
    NoReceipt,
    NoCategory,
    NotMapped,
    NoReceiptNoCategory,
    NoAmount,
}

/// <summary><see cref="Account"/> is the entry's Holded code when it is an open account in the chart.
/// <see cref="HoldedEntry"/> is the 57200003 entry it was found on, when booked.</summary>
internal sealed record PleoSpendRow(
    PleoEntryInfo Entry, LocalDate Date, PleoSpendState State, int? Account, int? HoldedEntry)
{
    public decimal Amount => Entry.AmountEur ?? 0m;
    public bool IsRefund => Amount > 0m;
}

/// <summary>A wallet load and the Sabadell → Pleo entry it was matched to, if any.</summary>
internal sealed record PleoTopUpRow(string PleoId, LocalDate Date, decimal Amount, int? HoldedEntry);

/// <summary>The classified mirror, before any display shaping.</summary>
internal sealed record PleoLedgerView(IReadOnlyList<PleoTopUpRow> TopUps, IReadOnlyList<PleoSpendRow> Spend);

internal enum PleoBookingOutcome { Posted, Unconfirmed, Failed, Skipped }

/// <summary>One line of a booking run. Amount is signed as in Pleo (− spend, + refund).</summary>
internal sealed record PleoBookingLine(
    string PleoId, LocalDate Date, string? Merchant, int Account, decimal Amount,
    PleoBookingOutcome Outcome, int? HoldedEntry, string? Error);

/// <summary>A booking run, current or last. <see cref="Refused"/> says why nothing (more) was booked.</summary>
internal sealed record PleoBookingRunVm(
    Instant StartedAt, Instant? FinishedAt, int? Requested, int Total, IReadOnlyList<PleoBookingLine> Lines,
    string? Refused)
{
    public bool IsRunning => FinishedAt is null;
}

internal sealed record PleoAccountTotalVm(int AccountNum, string Name, int Count, decimal Total);

/// <summary>/Finance/Pleo. <see cref="Holded"/> is 57200003's Σdebit − Σcredit, so cash held is positive, as is
/// <see cref="Wallet"/>.</summary>
internal sealed record PleoReconcileVm(
    PleoWalletBalance? Wallet,
    decimal Holded,
    Instant? LedgerSyncedAt,
    bool HoldedConfigured,
    PleoLedgerView View,
    IReadOnlyList<PleoAccountTotalVm> ReadyByAccount,
    IReadOnlyList<string> PendingIds,
    PleoBookingRunVm? Run)
{
    public decimal? Difference => Wallet is { } w ? w.CurrentEur - Holded : null;
}

/// <summary>The /Finance card: both balances, null where unknown.</summary>
internal sealed record FinancePleoVm(decimal? Wallet, decimal? Holded)
{
    public decimal? Difference => Wallet is { } w && Holded is { } h ? w - h : null;
}
