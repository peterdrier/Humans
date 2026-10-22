namespace Humans.GoogleIntegration.Services.Workspace;

internal interface ITeamResourceGoogleClient
{
    /// <summary>
    /// Also builds the display path, saving a round-trip. Only the stub reads <paramref name="expectFolder"/>; the
    /// real connector uses Drive's MIME type.
    /// </summary>
    Task<DriveLookupResult> GetDriveItemAsync(
        string itemId,
        bool expectFolder,
        CancellationToken ct = default);

    Task<GroupLookupResult> LookupGroupAsync(string groupEmail, CancellationToken ct = default);

    /// <summary>For sharing instructions when a link fails on permissions. The stub returns a placeholder.</summary>
    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);
}

/// <summary>Exactly one of <see cref="Item"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DriveLookupResult(DriveItem? Item, GoogleClientError? Error);

/// <summary>Exactly one of <see cref="Group"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record GroupLookupResult(ResolvedGroup? Group, GoogleClientError? Error);

/// <summary><c>FullPath</c> falls back to <c>Name</c> when ancestor access is denied.</summary>
internal sealed record DriveItem(
    string Id,
    string Name,
    string? WebViewLink,
    bool IsFolder,
    string FullPath);

/// <summary><c>DisplayUrl</c> is built by the connector, so the service needn't know the Workspace domain.</summary>
internal sealed record ResolvedGroup(string NumericId, string NormalizedEmail, string DisplayName, string DisplayUrl);

/// <summary><c>StatusCode</c> is 0 for transport errors; <c>RawMessage</c> is safe to log.</summary>
internal sealed record GoogleClientError(int StatusCode, string? RawMessage)
{
    /// <summary>
    /// 400 (no Google account, notification-free domain) or 403 "Cannot share a shared drive with non Google accounts".
    /// Other 403s are caller-permission failures and fail the phrase test.
    /// </summary>
    public bool IsDriveTargetRejection => StatusCode is 400 or 403 && RawMessage is { } message
        && (message.Contains("does not have a google account", StringComparison.OrdinalIgnoreCase)
            || message.Contains("non google account", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no google account", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not a google account", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not associated with a google account", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sendnotificationemail", StringComparison.OrdinalIgnoreCase));

    /// <summary>Worth retrying in the same pass: 5xx, 429, or Drive's 400 "internal error".</summary>
    public bool IsTransient => StatusCode is >= 500 or 429 || IsInternalError;

    /// <summary>Drive's 400 "Sorry, an internal error has occurred": usually a hiccup, but also how Drive refuses
    /// some plus addresses on custom domains.</summary>
    public bool IsInternalError => StatusCode == 400 && RawMessage is { } message
        && message.Contains("internal error has occurred", StringComparison.OrdinalIgnoreCase);
}
