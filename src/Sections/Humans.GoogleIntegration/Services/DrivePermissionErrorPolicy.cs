namespace Humans.GoogleIntegration.Services;

/// <summary>Google API status codes that mark a grant as permanently failed rather than retryable.</summary>
internal static class DrivePermissionErrorPolicy
{
    /// <summary>400 = invalid address, 403 = ineligible domain or recipient, 404 = not found.</summary>
    public static bool IsPermanentFailure(int? statusCode) => statusCode is 400 or 403 or 404;
}
