using System.Security.Claims;
using Humans.Backdoor.OAuth;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Backdoor.Services;

/// <summary>"Sign in with Humans" connections: approving one, listing them, revoking one.</summary>
internal interface IBackdoorConnectionService : IApplicationService
{
    /// <summary>
    /// Records the person's approval and returns the identity to sign in with, or null when no scope admits them. Audits
    /// and notifies the person, so a sign-in they did not start does not go unnoticed.
    /// </summary>
    Task<ClaimsIdentity?> ApproveAsync(
        Guid userId, BackdoorOAuthClient client, string resource, CancellationToken ct = default);

    /// <summary>Every connection that still works, unordered.</summary>
    Task<IReadOnlyList<BackdoorConnectionRow>> ListAsync(CancellationToken ct = default);

    /// <summary>Stops the connection and its tokens at once. Returns false if it is missing or already revoked.</summary>
    Task<bool> RevokeAsync(Guid connectionId, Guid actorUserId, CancellationToken ct = default);
}

internal sealed record BackdoorConnectionRow(Guid Id, Guid UserId, string ClientName, Instant CreatedAt);
