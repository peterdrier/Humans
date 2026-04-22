namespace Humans.Application.Interfaces;

/// <summary>
/// Narrow connector over the Google Drive Activity API v2 and the Admin
/// Directory API, scoped to the read-only operations performed by
/// <see cref="IDriveActivityMonitorService"/>. Implementations live in
/// <c>Humans.Infrastructure</c> (real Google-backed implementation for
/// production, stub for dev without service-account credentials). The
/// Application-layer service depends only on this interface so the
/// <c>Humans.Application</c> project stays free of <c>Google.Apis.*</c>
/// imports inside business logic (design-rules §13).
/// </summary>
public interface IGoogleDriveActivityClient
{
    /// <summary>
    /// Returns the service account's primary email address (the <c>client_email</c>
    /// field of the configured service-account key), falling back to a sentinel
    /// <c>unknown@serviceaccount.iam.gserviceaccount.com</c> if the key is not
    /// available. Used to filter out self-initiated permission changes.
    /// </summary>
    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the service account's OAuth2 client_id (the <c>client_id</c> field
    /// of the configured service-account key), or <c>null</c> when no key is
    /// available. The Drive Activity API often identifies service-account actors
    /// as <c>people/{client_id}</c>, so the monitor uses this to detect
    /// self-initiated changes.
    /// </summary>
    Task<string?> GetServiceAccountClientIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Enumerates Drive Activity events for a single Shared-Drive resource whose
    /// event timestamp is at or after <paramref name="sinceIsoTimestamp"/>. The
    /// enumeration pages internally and yields activities in the order returned
    /// by the Drive Activity API (reverse chronological).
    /// </summary>
    /// <param name="googleItemId">
    /// The Drive Activity API resource ID. Callers pass the Google-side ID of
    /// the shared-drive item (folder or file). The connector prefixes
    /// <c>items/</c> internally.
    /// </param>
    /// <param name="sinceIsoTimestamp">
    /// Lower-bound filter applied as <c>time &gt;= "{value}"</c> on the query.
    /// The caller formats as an invariant-culture ISO-8601 instant.
    /// </param>
    /// <remarks>
    /// May throw <see cref="DriveActivityResourceNotFoundException"/> when the
    /// underlying Drive item has been deleted.
    /// </remarks>
    IAsyncEnumerable<DriveActivityEvent> QueryActivityAsync(
        string googleItemId,
        string sinceIsoTimestamp,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a Drive Activity <c>people/{id}</c> identifier to its primary
    /// email address via the Google Admin Directory API. Returns <c>null</c>
    /// when the user is not found (404), the caller is unauthorized (403), or
    /// any other transient failure occurs (logged by the implementation).
    /// </summary>
    Task<string?> TryResolvePersonEmailAsync(string peopleId, CancellationToken ct = default);
}

/// <summary>
/// Raised by <see cref="IGoogleDriveActivityClient.QueryActivityAsync"/> when
/// the Drive item the caller asked about has been deleted (HTTP 404). Callers
/// typically downgrade this to a warning and move on.
/// </summary>
public sealed class DriveActivityResourceNotFoundException : Exception
{
    public DriveActivityResourceNotFoundException()
    {
    }

    public DriveActivityResourceNotFoundException(string googleItemId)
        : base($"Drive item '{googleItemId}' not found (404)")
    {
        GoogleItemId = googleItemId;
    }

    public DriveActivityResourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? GoogleItemId { get; }
}
