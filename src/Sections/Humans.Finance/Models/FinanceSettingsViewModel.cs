namespace Humans.Finance.Models;

/// <summary>One row per unarchived 441 ledger account; <see cref="StripeAccounts"/> are the ids the Stripe mirror
/// holds a balance for.</summary>
internal sealed record FinanceSettingsViewModel(
    IReadOnlyList<FinanceStripeLinkRow> Rows, IReadOnlyList<string> StripeAccounts);

internal sealed record FinanceStripeLinkRow(int Index, int LedgerAccountNum, string LedgerAccountName, string? StripeAccountId);

internal sealed record FinanceStripeLinkInput(int LedgerAccountNum, string? StripeAccountId);
