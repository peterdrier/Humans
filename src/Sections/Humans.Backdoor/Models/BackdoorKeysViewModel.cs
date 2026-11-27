using NodaTime;

namespace Humans.Backdoor.Models;

internal sealed class BackdoorKeysViewModel
{
    /// <summary>Every key for Admin and Board; only the viewer's own for anyone else.</summary>
    public IReadOnlyList<BackdoorKeyListItem> Keys { get; init; } = [];

    /// <summary>Who an Admin may create a key for. Empty for anyone else, who only makes their own.</summary>
    public IReadOnlyList<BackdoorKeyCandidate> EligibleUsers { get; init; } = [];

    /// <summary>Shown once: survives exactly one redirect, in TempData. Null on a plain page load.</summary>
    public string? NewPlaintextKey { get; init; }

    public int ShortKeyLifetimeDays { get; init; }

    /// <summary>Admin and Board read every key, so the list names each owner.</summary>
    public bool ShowsOwners { get; init; }

    /// <summary>Working "Sign in with Humans" connections, scoped like <see cref="Keys"/>.</summary>
    public IReadOnlyList<BackdoorConnectionListItem> Connections { get; init; } = [];

    /// <summary>The address and client id a person types into Claude's connector dialog.</summary>
    public string McpAddress { get; init; } = string.Empty;

    public string ClaudeClientId { get; init; } = string.Empty;
}

internal sealed record BackdoorConnectionListItem(
    Guid Id, Guid UserId, string ClientName, Instant CreatedAt, bool ViewerMayManage);

internal sealed record BackdoorKeyListItem(
    Guid Id,
    Guid UserId,
    string Label,
    string DisplayPrefix,
    Instant CreatedAt,
    Instant? LastUsedAt,
    Instant? RevokedAt,
    Instant? ExpiresAt,
    bool OwnerEligible,
    bool IsExpired,
    bool ViewerMayManage)
{
    public bool IsRevoked => RevokedAt is not null;

    /// <summary>
    /// Not the same as unrevoked: eligibility is re-checked on every request, so a key whose owner was suspended or lost
    /// every scope is refused with no <c>RevokedAt</c>, and so is one past its expiry.
    /// </summary>
    public bool IsUsable => RevokedAt is null && OwnerEligible && !IsExpired;

    /// <summary>Rotating an expired key is how it is renewed, so only revocation or a lost scope withholds it.</summary>
    public bool IsRotatable => ViewerMayManage && RevokedAt is null && OwnerEligible;
}

internal sealed record BackdoorKeyCandidate(Guid UserId, string DisplayName);
