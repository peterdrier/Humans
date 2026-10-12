using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>Distinguishes a rejected Drive recipient from caller permissions and sharing policy errors.</summary>
internal static class DrivePermissionErrorPolicy
{
    public static bool IsPermanentFailure(int? statusCode) => statusCode is 400 or 403 or 404;

    public static bool IsTargetRejection(GoogleClientError? error) => error is { StatusCode: 400 or 403, RawMessage: { } message } &&
        (message.Contains("does not have a google account", StringComparison.OrdinalIgnoreCase)
        || message.Contains("non google account", StringComparison.OrdinalIgnoreCase)
        || message.Contains("no google account", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not a google account", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not associated with a google account", StringComparison.OrdinalIgnoreCase)
        || message.Contains("sendnotificationemail", StringComparison.OrdinalIgnoreCase));
}
