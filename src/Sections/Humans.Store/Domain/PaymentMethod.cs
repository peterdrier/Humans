namespace Humans.Store.Domain;

internal enum PaymentMethod
{
    Stripe = 0,
    BankTransfer = 1,
    Manual = 2,
    /// <summary>A returned deposit (full or partial) credited back to the order by a Store admin. Positive.</summary>
    DepositReturn = 3,
    /// <summary>Money sent back to the camp by hand from the Stripe dashboard, recorded by a Store admin. Negative.</summary>
    Refund = 4
}
