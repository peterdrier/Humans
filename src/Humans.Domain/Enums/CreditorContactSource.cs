namespace Humans.Domain.Enums;

/// <summary>How a member's Holded creditor-contact binding came to be.</summary>
public enum CreditorContactSource
{
    /// <summary>Created automatically when the member's first expense report was pushed to Holded.</summary>
    Auto,

    /// <summary>Bound to an existing Holded creditor account by a finance admin.</summary>
    Manual
}
