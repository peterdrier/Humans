using Humans.Pleo.Contracts;
using NodaTime;

namespace Humans.Finance.Models;

/// <summary>Where one Pleo spend entry stands against Holded.</summary>
internal enum PleoSpendState
{
    /// <summary>Its <c>Pleo-{Id}</c> purchase is approved and paid, or a 57200003 line names it.</summary>
    Booked,
    /// <summary>Its purchase exists but is not yet approved or not fully paid: the next run finishes it.</summary>
    Unfinished,
    /// <summary>A create was sent but Holded does not list the purchase: never sent again; check in Holded.</summary>
    CheckInHolded,
    Ready,
    NoCategory,
    NotMapped,
    NoAmount,
}

/// <summary><see cref="Account"/> is the entry's Holded code when it is an open account in the chart.
/// <see cref="Purchase"/> is its <c>Pleo-{Id}</c> purchase, when Holded has one.</summary>
internal sealed record PleoSpendRow(
    PleoEntryInfo Entry, LocalDate Date, PleoSpendState State, int? Account, PleoPurchaseRef? Purchase)
{
    public decimal Amount => Entry.AmountEur ?? 0m;
    public bool IsRefund => Amount > 0m;
}

/// <summary>A <c>Pleo-{Id}</c> purchase as the purchase mirror holds it. <see cref="PaymentsPending"/> is null when
/// the mirror has not read it.</summary>
internal sealed record PleoPurchaseRef(
    string DocumentId, string Number, bool Approved, decimal? PaymentsPending, string HoldedUrl)
{
    public bool Complete => Approved && PaymentsPending == 0m;
}

/// <summary>A wallet load and the Sabadell → Pleo entry it was matched to, if any.</summary>
internal sealed record PleoTopUpRow(string PleoId, LocalDate Date, decimal Amount, int? HoldedEntry);

/// <summary>A refund and the charge it most likely refunds (display only, never used to post).
/// <see cref="CategoryDiffers"/> flags a refund booked on another category than that charge.</summary>
internal sealed record PleoRefundOrigin(string RefundId, PleoEntryInfo Charge, bool CategoryDiffers);

/// <summary>A booked purchase whose Pleo entry has receipts Holded has not been sent.</summary>
internal sealed record PleoReceiptsWaiting(PleoSpendRow Row, IReadOnlyList<string> ReceiptIds);

/// <summary>One journal entry that brings Holded in line with Pleo: Dr <see cref="Debit"/> / Cr <see cref="Credit"/>.</summary>
internal sealed record PleoCorrection(int Debit, int Credit, decimal Amount, string Description);

/// <summary>A booked Pleo entry Holded no longer agrees with. <see cref="Entry"/> is null when Pleo no longer lists it
/// as settled. <see cref="Holded"/> is the net (Σdebit − Σcredit) per 629 account on lines naming the id;
/// <see cref="Corrections"/> is empty when it is only listed (gone from Pleo, or no category now).</summary>
internal sealed record PleoChangeRow(
    string PleoId, PleoEntryInfo? Entry, IReadOnlyDictionary<int, decimal> Holded, string What,
    IReadOnlyList<PleoCorrection> Corrections);

/// <summary>The classified mirrors, before any display shaping. <see cref="Unattributed"/> are 57200003 lines that
/// name no Pleo id and are not a Sabadell top-up: not from Humans, check them.</summary>
internal sealed record PleoLedgerView(
    IReadOnlyList<PleoTopUpRow> TopUps,
    IReadOnlyList<PleoSpendRow> Spend,
    IReadOnlyList<PleoDuplicate> Duplicates,
    IReadOnlyList<Humans.Holded.Contracts.HoldedLedgerLineInfo> Unattributed,
    IReadOnlyList<PleoRefundOrigin> RefundOrigins,
    IReadOnlyList<PleoReceiptsWaiting> ReceiptsWaiting,
    IReadOnlyList<PleoChangeRow> Changes,
    IReadOnlySet<string> UnconfirmedReceipts);

/// <summary>A Pleo id with more than one <c>Pleo-{Id}</c> purchase: delete the extra one in Holded.</summary>
internal sealed record PleoDuplicate(string PleoId, IReadOnlyList<string> HoldedUrls);

internal enum PleoRunKind { Book, Receipts, Corrections }

internal enum PleoBookingOutcome { Posted, Unconfirmed, Failed, Skipped }

/// <summary>One line of a run. Amount is signed as in Pleo (− spend, + refund). <see cref="Receipts"/> counts the
/// receipts attached to the purchase in this run.</summary>
internal sealed record PleoBookingLine(
    string PleoId, LocalDate Date, string? Merchant, int Account, decimal Amount,
    PleoBookingOutcome Outcome, string? HoldedUrl, int Receipts, string? Error);

/// <summary>A run, current or last. <see cref="Refused"/> says why nothing (more) was done.</summary>
internal sealed record PleoBookingRunVm(
    PleoRunKind Kind, Instant StartedAt, Instant? FinishedAt, int Total,
    IReadOnlyList<PleoBookingLine> Lines, string? Refused)
{
    public bool IsRunning => FinishedAt is null;
}

internal sealed record PleoAccountTotalVm(int AccountNum, string Name, int Count, decimal Total);

/// <summary>/Finance/Pleo. <see cref="Holded"/> is 57200003's Σdebit − Σcredit, so cash held is positive, as is
/// <see cref="Wallet"/>. <see cref="NotConfigured"/> names the missing settings, null when booking can run.</summary>
internal sealed record PleoReconcileVm(
    PleoWalletBalance? Wallet,
    decimal Holded,
    Instant? LedgerSyncedAt,
    string? NotConfigured,
    PleoLedgerView View,
    IReadOnlyList<PleoAccountTotalVm> ReadyByAccount,
    IReadOnlyDictionary<int, string> AccountNames,
    PleoBookingRunVm? Run)
{
    public decimal? Difference => Wallet is { } w ? w.CurrentEur - Holded : null;
}
