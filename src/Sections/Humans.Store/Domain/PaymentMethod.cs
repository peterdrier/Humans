namespace Humans.Store.Domain;

internal enum PaymentMethod
{
    Stripe = 0,
    /// <summary>Received in the bank and recorded by a Finance admin, citing the Holded bank movement id. Positive.</summary>
    BankTransfer = 1,
    Manual = 2,
    /// <summary>Credited back to the order by a Store admin. Positive.</summary>
    DepositReturn = 3,
    /// <summary>Sent back by hand from the Stripe dashboard and recorded by a Store admin. Negative.</summary>
    Refund = 4
}
