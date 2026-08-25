namespace Humans.Finance.Models;

/// <summary>One row of the payout the admin ticked, as posted from /Finance/Creditors.</summary>
internal sealed record SepaPayoutSelection(int SupplierAccountNum, decimal Amount);

/// <summary>
/// The generated file, or the reason there is none. All-or-nothing: one bad row refuses the whole
/// generation, because a partially-sent batch is far harder to reconcile than a re-run.
/// </summary>
internal sealed record SepaPayoutResult(string? FileName, string? Xml, string? ErrorMessage)
{
    public static SepaPayoutResult Failure(string message) => new(null, null, message);

    public bool Succeeded => ErrorMessage is null;
}

/// <summary>Whether SEPA generation is available at all, and the ceiling it enforces.</summary>
/// <param name="UnavailableReason">Null when configured; otherwise what the admin must set.</param>
internal sealed record SepaPayoutSettings(decimal MaxPerTransfer, string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;
}
