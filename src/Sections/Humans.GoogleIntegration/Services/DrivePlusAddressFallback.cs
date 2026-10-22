using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Drive can refuse a non-Gmail <c>local+tag@domain</c> although the base address is a Google account. Gmail plus
/// addresses are canonicalized before any Google call; other Google services stay on the plus address.
/// </summary>
internal static class DrivePlusAddressFallback
{
    /// <summary><c>local@domain</c> for <c>local+tag@domain</c>; null when there is no tag.</summary>
    public static string? BaseAddress(string email)
    {
        var at = email.LastIndexOf('@');
        var plus = at <= 0 ? -1 : email.IndexOf('+', 0, at);
        return plus <= 0 ? null : string.Concat(email.AsSpan(0, plus), email.AsSpan(at));
    }

    /// <summary>The recorded Drive address for the member while it still stands in for
    /// <paramref name="identityEmail"/>, else <paramref name="email"/>.</summary>
    public static string Apply(
        IReadOnlyDictionary<Guid, DriveEmailOverride> overrides, Guid userId, string identityEmail, string email) =>
        overrides.TryGetValue(userId, out var drive)
            && string.Equals(drive.IdentityEmail, identityEmail, StringComparison.OrdinalIgnoreCase)
            ? drive.DriveEmail
            : email;

    /// <summary>
    /// When Drive refuses the plus address itself (not an outage, rate limit or caller-permission failure), grants the
    /// base address and records it on the identity. If the base address fails too, returns the original failure.
    /// </summary>
    public static async Task<(DrivePermissionMutationResult Result, string Email)> CreateAsync(
        Func<string, Task<DrivePermissionMutationResult>> create, string email,
        Guid userId, string identityEmail, IWorkspaceIdentityManagement identities, CancellationToken ct)
    {
        var result = await create(email);
        if (result.Error is not ({ IsDriveTargetRejection: true } or { IsInternalError: true })
            || BaseAddress(email) is not { } baseEmail)
            return (result, email);

        var fallback = await create(baseEmail);
        if (fallback.Outcome == DrivePermissionCreateOutcome.Failed)
            return (result, email);

        await identities.SetDriveEmailFromSyncAsync(userId, identityEmail, baseEmail, ct);
        return (fallback, baseEmail);
    }
}
