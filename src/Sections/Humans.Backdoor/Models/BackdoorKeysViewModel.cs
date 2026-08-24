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
    Instant? RevokedAt,
    bool OwnerEligible)
{
    public bool IsRevoked => RevokedAt is not null;

    /// <summary>
    /// Whether the key actually opens the API. Not the same as unrevoked: eligibility is
    /// re-checked on every request, so a key whose owner was suspended or lost Admin/Board is
    /// refused while its row still carries no <c>RevokedAt</c>.
    /// </summary>
    public bool IsUsable => RevokedAt is null && OwnerEligible;
}

internal sealed record BackdoorKeyCandidate(Guid UserId, string DisplayName);
