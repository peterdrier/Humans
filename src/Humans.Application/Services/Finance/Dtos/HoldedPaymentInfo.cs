using NodaTime;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>One Holded payment row exposed to read consumers (per-member ledger).</summary>
public sealed record HoldedPaymentInfo(LocalDate Date, decimal Amount, string? DocumentType);
