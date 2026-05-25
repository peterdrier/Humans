using NodaTime;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>Cached creditor status for one member, sourced from Holded.</summary>
public sealed record HoldedCreditorStatus(
    int? SupplierAccountNum,
    decimal Balance,            // signed; negative = org owes the member
    decimal OwedToMember,       // = max(0, -Balance)
    LocalDate? LastPaymentDate,
    decimal TotalPaid);
