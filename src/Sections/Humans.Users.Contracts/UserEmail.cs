using NodaTime;

namespace Humans.Users.Contracts;

public class UserEmail
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>
    /// Only <c>UserEmailService.ReconcileOAuthIdentityAsync</c>, matched on <see cref="Provider"/> and
    /// <see cref="ProviderKey"/> from the OAuth callback, rewrites an existing row's address. Everything else adds or
    /// removes rows (<c>memory/architecture/email-mutation-paths.md</c>).
    /// </summary>
    public string Email { get; set; } = string.Empty;

    public bool IsVerified { get; set; }

    /// <summary>
    /// The OAuth provider owning this row ("Google"); null when none is linked. One row per (Provider, ProviderKey),
    /// enforced in <c>UserEmailService</c>, not the database.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>OIDC <c>sub</c>: stable across Workspace renames, so the only legitimate key for rewriting <see cref="Email"/>.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>
    /// Frozen: the Workspace identity is GoogleIntegration's. Written only to clear it on rows a merge moves; read
    /// only by the <c>google.workspace-identities</c> copy and fallback. Dropped once that copy is done.
    /// </summary>
    public bool IsGoogle { get; set; }

    /// <summary>Frozen: read only by the <c>google.workspace-identities</c> copy and fallback.</summary>
    public LegacyGoogleEmailStatus GoogleEmailStatus { get; set; } = LegacyGoogleEmailStatus.Unknown;

    /// <summary>The notification target. Exactly one per user, enforced in <c>UserEmailService</c>, not the database.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Null hides it from the profile.</summary>
    public ContactFieldVisibility? Visibility { get; set; }

    /// <summary>For the resend cooldown.</summary>
    public Instant? VerificationSentAt { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
