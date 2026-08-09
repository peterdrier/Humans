using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>One Holded payment row exposed to read consumers (per-member ledger).</summary>
public sealed record HoldedPaymentInfo(LocalDate Date, decimal Amount, string? DocumentType);
