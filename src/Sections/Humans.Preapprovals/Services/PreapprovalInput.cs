using Humans.Preapprovals.Contracts;

namespace Humans.Preapprovals.Services;

internal static class PreapprovalInput
{
    private const decimal MaxStoredAmount = 9_999_999_999_999_999.99m;

    internal static PreapprovalRefusal? MoneyRefusal(decimal amount) =>
        amount <= 0 ? new(PreapprovalErrorKeys.AmountPositive)
        : amount > MaxStoredAmount ? new(PreapprovalErrorKeys.AmountTooLarge)
        : decimal.Round(amount, 2) != amount ? new(PreapprovalErrorKeys.AmountPrecision)
        : null;

    internal static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
