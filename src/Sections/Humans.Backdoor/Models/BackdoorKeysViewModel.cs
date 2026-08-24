using NodaTime;

namespace Humans.Backdoor.Models;

/// <summary>The admin key-management page: every key, plus who a new one may go to.</summary>
internal sealed class BackdoorKeysViewModel
{
    public IReadOnlyList<BackdoorKeyListItem> Keys { get; init; } = [];

    /// <summary>Full Admins and Board members — the only people who may hold a key.</summary>
    public IReadOnlyList<BackdoorKeyCandidate> EligibleUsers { get; init; } = [];

    /// <summary>
    /// The plaintext of a key just issued or rotated, shown once and never again. Null on a
    /// plain page load — it survives exactly one redirect, in TempData.
    /// </summary>
    public string? NewPlaintextKey { get; init; }
}

internal sealed record BackdoorKeyListItem(
    Guid Id,
    Guid UserId,
    string OwnerName,
    string Label,
    string DisplayPrefix,
    Instant CreatedAt,
    Instant? LastUsedAt,
    Instant? RevokedAt)
{
    public bool IsActive => RevokedAt is null;
}

internal sealed record BackdoorKeyCandidate(Guid UserId, string DisplayName);
