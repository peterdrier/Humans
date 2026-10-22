using NodaTime;

namespace Humans.Backdoor.Domain;

/// <summary>
/// The plaintext key is never stored. Revocation stamps <see cref="RevokedAt"/> and rotation inserts a new row, so who
/// held a key when stays answerable; a row is deleted only when its owner is erased.
/// </summary>
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
    /// Nullable so GDPR erasure can detach a deleted admin from a key that belongs to someone else. The audit log keeps
    /// who issued what.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Stamped on every authenticated request, so dormant keys are visible.</summary>
    public Instant? LastUsedAt { get; set; }

    public Instant? RevokedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }

    public bool IsActive => RevokedAt is null;
}
