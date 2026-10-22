using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>Append-only: DB triggers reject UPDATE and DELETE.</summary>
internal sealed class ConsentRecord
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public Guid DocumentVersionId { get; init; }

    public DocumentVersion DocumentVersion { get; set; } = null!;

    public Instant ConsentedAt { get; init; }

    public string IpAddress { get; init; } = string.Empty;

    public string UserAgent { get; init; } = string.Empty;

    /// <summary>SHA-256 hex of the canonical Spanish content at consent time.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Always true for a valid record.</summary>
    public bool ExplicitConsent { get; init; }
}
