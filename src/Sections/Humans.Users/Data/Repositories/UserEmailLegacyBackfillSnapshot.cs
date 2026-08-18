namespace Humans.Users.Data.Repositories;

/// <summary>
/// Snapshot of a <c>user_emails</c> row including the legacy <c>IsOAuth</c>
/// shadow column. Used by the one-shot
/// <c>UserEmailProviderBackfillService</c> to read the legacy column without
/// reaching back into the deleted CLR property — the snapshot is built via
/// <c>EF.Property&lt;bool&gt;(e, "IsOAuth")</c> so the legacy column stays
/// readable until it is dropped in a deferred PR.
/// </summary>
internal sealed record UserEmailLegacyBackfillSnapshot(
    Guid Id,
    Guid UserId,
    string Email,
    bool IsVerified,
    string? Provider,
    string? ProviderKey,
    bool IsGoogle,
    bool LegacyIsOAuth);
