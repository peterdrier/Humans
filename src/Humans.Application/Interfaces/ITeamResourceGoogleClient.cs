namespace Humans.Application.Interfaces;

/// <summary>
/// Narrow connector over the Google Workspace Drive and Cloud Identity APIs,
/// scoped to the operations <c>TeamResourceService</c> performs when linking
/// pre-shared resources. Implementations live in <c>Humans.Infrastructure</c>
/// (real Google-backed implementation for production, stub for dev without
/// service-account credentials). The Application-layer service depends only
/// on this interface so the <c>Humans.Application</c> project can stay
/// framework-free (no <c>Google.Apis.*</c> imports inside business logic).
/// </summary>
public interface ITeamResourceGoogleClient
{
    /// <summary>
    /// Looks up a Google Drive item (file or folder) by its id and returns a
    /// shape-neutral DTO. Walking the parent chain to build a display path is
    /// included so the application service does not need another round-trip
    /// through the connector.
    /// </summary>
    /// <param name="itemId">The Drive-assigned id to look up.</param>
    /// <param name="expectFolder">
    /// True when the caller parsed the URL as a folder URL, false when it was
    /// parsed as a file URL. The real connector ignores the hint and uses the
    /// MIME type returned by Drive; the stub uses it to fabricate a
    /// matching-type response (it cannot call Google to discover the real MIME
    /// type).
    /// </param>
    /// <returns>
    /// A <see cref="DriveItem"/> on success, or a <see cref="GoogleClientError"/>
    /// describing why the lookup failed (not-found, permission-denied, or
    /// transport error). Exactly one of the two is non-null.
    /// </returns>
    Task<DriveLookupResult> GetDriveItemAsync(
        string itemId,
        bool expectFolder,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up a Google Group by its email address. Returns the resolved
    /// numeric id, display name, and email normalized by the connector; or a
    /// <see cref="GoogleClientError"/> on failure.
    /// </summary>
    Task<GroupLookupResult> LookupGroupAsync(string groupEmail, CancellationToken ct = default);

    /// <summary>
    /// Returns the email address of the service account that backs the
    /// connector. Used by the service to render sharing instructions when a
    /// link attempt fails due to missing permissions. Connectors that do not
    /// have a real service account (the stub) return a dev-friendly
    /// placeholder.
    /// </summary>
    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);
}

/// <summary>
/// Outcome of a Drive item lookup. Exactly one of <see cref="Item"/> or
/// <see cref="Error"/> is non-null.
/// </summary>
/// <param name="Item">The resolved Drive item, when the lookup succeeded.</param>
/// <param name="Error">
/// The failure description, when the lookup did not succeed. <see cref="GoogleClientError.StatusCode"/>
/// carries the HTTP status (404 / 403 / other) so the service can produce
/// context-appropriate user-facing copy.
/// </param>
public record DriveLookupResult(DriveItem? Item, GoogleClientError? Error);

/// <summary>
/// Outcome of a Google Group lookup. Exactly one of <see cref="Group"/> or
/// <see cref="Error"/> is non-null.
/// </summary>
public record GroupLookupResult(ResolvedGroup? Group, GoogleClientError? Error);

/// <summary>
/// Shape-neutral representation of a Drive item (file, folder, or shared-drive
/// root) returned by <see cref="ITeamResourceGoogleClient"/>.
/// </summary>
/// <param name="Id">The Drive-assigned id. Matches the input id.</param>
/// <param name="Name">The display name of the item.</param>
/// <param name="WebViewLink">The <c>webViewLink</c> URL, if one was returned.</param>
/// <param name="IsFolder">True when the item's MIME type is the folder type.</param>
/// <param name="FullPath">
/// Human-readable path built by walking the parent chain; e.g.
/// <c>"SharedDrive / Parent / Child"</c>. Falls back to <see cref="Name"/>
/// when ancestor access is denied.
/// </param>
public record DriveItem(
    string Id,
    string Name,
    string? WebViewLink,
    bool IsFolder,
    string FullPath);

/// <summary>
/// Shape-neutral representation of a resolved Google Group.
/// </summary>
/// <param name="NumericId">The group's numeric id (e.g. <c>01a2b3c4</c>).</param>
/// <param name="NormalizedEmail">The group email normalized for storage/display.</param>
/// <param name="DisplayName">The group's display name (for audit messages).</param>
/// <param name="DisplayUrl">
/// Pre-built "view this group" URL, e.g.
/// <c>https://groups.google.com/a/{domain}/g/{local}</c>. Connector-owned so
/// the application service does not need to know the Workspace domain.
/// </param>
public record ResolvedGroup(string NumericId, string NormalizedEmail, string DisplayName, string DisplayUrl);

/// <summary>
/// Failure description returned by the connector when a Google API call
/// failed. The service uses <see cref="StatusCode"/> to pick a user-facing
/// message and <see cref="RawMessage"/> for structured logging.
/// </summary>
/// <param name="StatusCode">The HTTP status returned by Google, or <c>0</c> for transport errors.</param>
/// <param name="RawMessage">The underlying error message, safe to log.</param>
public record GoogleClientError(int StatusCode, string? RawMessage);
