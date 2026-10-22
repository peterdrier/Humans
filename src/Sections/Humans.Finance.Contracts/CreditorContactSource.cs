namespace Humans.Finance.Contracts;

public enum CreditorContactSource
{
    /// <summary>Created when the member's first expense report was pushed to Holded.</summary>
    Auto,

    /// <summary>Bound by a finance admin to an existing Holded creditor account.</summary>
    Manual
}
