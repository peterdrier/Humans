using NodaTime;

namespace Humans.Backdoor.Models;

internal sealed class BackdoorKeysViewModel
{
    public IReadOnlyList<BackdoorKeyListItem> Keys { get; init; } = [];

    /// <summary>Full Admins and Board members — the only people who may hold a key.</summary>
    public IReadOnlyList<BackdoorKeyCandidate> EligibleUsers { get; init; } = [];

    /// <summary>Shown once: survives exactly one redirect, in TempData. Null on a plain page load.</summary>
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
    /// Not the same as unrevoked: eligibility is re-checked on every request, so a key whose owner was suspended or lost
    /// Admin/Board is refused with no <c>RevokedAt</c>.
    /// </summary>
    public bool IsUsable => RevokedAt is null && OwnerEligible;
}

internal sealed record BackdoorKeyCandidate(Guid UserId, string DisplayName);
