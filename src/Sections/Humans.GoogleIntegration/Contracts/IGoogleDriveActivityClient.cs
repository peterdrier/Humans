namespace Humans.GoogleIntegration.Contracts;

public interface IGoogleDriveActivityClient
{
    /// <summary>
    /// False for the dev stub. The monitor must not advance its last-run marker then: the stub returns no events,
    /// so advancing would skip real history once credentials arrive.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>The key's <c>client_email</c>, or a sentinel when there is no key. Filters out self-initiated changes.</summary>
    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);

    /// <summary>
    /// The key's <c>client_id</c>, or null without a key. Drive Activity often names service-account actors
    /// <c>people/{client_id}</c>.
    /// </summary>
    Task<string?> GetServiceAccountClientIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Newest first, paged internally. <paramref name="googleItemId"/> is the Google-side id (the <c>items/</c> prefix
    /// is added here); <paramref name="sinceIsoTimestamp"/> is an invariant ISO-8601 instant. Throws
    /// <see cref="DriveActivityResourceNotFoundException"/> when the item was deleted.
    /// </summary>
    IAsyncEnumerable<DriveActivityEvent> QueryActivityAsync(
        string googleItemId,
        string sinceIsoTimestamp,
        CancellationToken ct = default);

    /// <summary>Null on 404, 403 or any transient failure (logged).</summary>
    Task<string?> TryResolvePersonEmailAsync(string peopleId, CancellationToken ct = default);
}

/// <summary>The Drive item was deleted (404). Callers usually log a warning and move on.</summary>
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
