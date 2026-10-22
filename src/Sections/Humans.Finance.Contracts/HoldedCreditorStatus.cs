using NodaTime;

namespace Humans.Finance.Contracts;

public sealed record HoldedCreditorStatus(
    int SupplierAccountNum,
    decimal Balance,            // signed; negative = org owes the member
    decimal OwedToMember,       // = max(0, -Balance)
    LocalDate? LastPaymentDate, // null when no payment has gone out yet
    decimal TotalPaid);
