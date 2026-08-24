using NodaTime;

namespace Humans.Backdoor.Domain;

/// <summary>
/// One personal machine-API credential. The plaintext key is never stored: issuance
/// returns it once and persists only <see cref="KeyHash"/> (SHA-256, lowercase hex) plus
/// <see cref="DisplayPrefix"/>, the leading characters kept so a human can tell their rows
/// apart in the admin list.
/// </summary>
/// <remarks>
/// Rows are append-only in spirit: revocation stamps <see cref="RevokedAt"/> rather than
/// deleting, and rotation revokes the old row and inserts a new one, so who held a key when
/// stays answerable after the fact.
/// </remarks>
internal sealed class BackdoorApiKey
{
    public Guid Id { get; set; }

    /// <summary>The human this key acts as. Every request it authenticates is attributed here.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the plaintext key, lowercase hex. Unique.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Leading characters of the plaintext, for identification in the admin list.</summary>
    public string DisplayPrefix { get; set; } = string.Empty;

    /// <summary>What the holder calls this key ("triage laptop", "CI"). Free text.</summary>
    public string Label { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    /// <summary>
    /// The admin who allocated the key. Nullable so GDPR erasure can detach a deleted admin
    /// from a key that still belongs to someone else — the same detach-don't-retain shape as
    /// <see cref="RevokedByUserId"/>. Who issued what is separately in the audit log.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Stamped on every authenticated request, so dormant keys are visible.</summary>
    public Instant? LastUsedAt { get; set; }

    public Instant? RevokedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }

    public bool IsActive => RevokedAt is null;
}
