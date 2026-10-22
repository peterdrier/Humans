using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// Kept verbatim: the bank's copy and ours must be byte-identical when a transfer is queried months later, so the XML
/// is the record, not columns it could be rebuilt from.
/// </summary>
internal sealed class SepaPayoutFile
{
    /// <summary>Also the source of <c>MsgId</c> in the file.</summary>
    public Guid Id { get; init; }

    public Instant GeneratedAt { get; init; }

    public Guid GeneratedByUserId { get; init; }

    /// <summary>So a file the treasurer still has on disk can be identified.</summary>
    public string FileName { get; init; } = "";

    /// <summary>SHA-256 of the UTF-8 XML, lowercase hex.</summary>
    public string Checksum { get; init; } = "";

    public string Xml { get; init; } = "";
}
