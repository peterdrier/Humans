using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Drive grants for a non-Gmail plus address (<c>local+tag@domain</c>), which Drive can refuse even
/// though the base address is a Google account (peterdrier/Humans#2037). Gmail plus addresses never
/// get here: they are canonicalized before any Google call. Groups and other Google services treat
/// plus addresses differently, so this stays Drive-only.
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
    /// Grants <paramref name="email"/>. When Drive refuses a plus address (a target rejection or
    /// its 400 internal error; never an outage, rate limit or caller-permission failure, which say
    /// nothing about the address), grants the base address
    /// instead and, if Drive accepts it, records it on the member's identity so later passes match and
    /// grant it directly. Returns the result to act on and the address it is for; when the base
    /// address fails too, that is the original failure.
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
