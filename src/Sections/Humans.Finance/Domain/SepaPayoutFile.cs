using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// One generated pain.001.001.09 payout file, kept verbatim. The bank's copy and ours must be
/// byte-identical when a transfer is queried months later, so the XML itself is the record —
/// not a set of columns it could be rebuilt from.
/// </summary>
/// <remarks>
/// Nothing here is stamped onto an expense report or a member: settlement closes through the
/// Holded booking and the next ledger sync zeroing the creditor balance.
/// </remarks>
internal sealed class SepaPayoutFile
{
    /// <summary>Also the source of <c>MsgId</c> and <c>PmtInfId</c> in the file.</summary>
    public Guid Id { get; init; }

    public Instant GeneratedAt { get; init; }

    /// <summary>The finance admin who pressed Generate. Bare FK (no nav).</summary>
    public Guid GeneratedByUserId { get; init; }

    /// <summary>Download name, so a file the treasurer still has on disk can be identified.</summary>
    public string FileName { get; init; } = "";

    /// <summary>SHA-256 of the UTF-8 XML, lowercase hex.</summary>
    public string Checksum { get; init; } = "";

    public string Xml { get; init; } = "";
}
