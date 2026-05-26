using NodaTime;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>Cached creditor status for one member, sourced from Holded.</summary>
public sealed record HoldedCreditorStatus(
    int? SupplierAccountNum,
    decimal? Balance,           // signed; negative = org owes the member. NULL = no cached balance row (unknown — NOT settled).
    decimal OwedToMember,       // = max(0, -Balance), or 0 when Balance is unknown
    LocalDate? LastPaymentDate,
    decimal TotalPaid);
