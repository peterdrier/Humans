using Humans.Base.Interfaces;

namespace Humans.Grants.Contracts;

/// <summary>
/// A source section filling the one open program with its source key. Any refusal, including no such program, is an
/// <see cref="InvalidOperationException"/>. Every call is audited with the actor and the source's reference.
/// </summary>
public interface IGrantWrite : IApplicationService
{
    /// <summary>
    /// Tells the person by email and in-app. Idempotent per person: an existing live grant's id is returned and
    /// nothing is sent again. <paramref name="sourceRef"/> is the caller's own id for this decision.
    /// </summary>
    Task<Guid> IssueAsync(
        string sourceKey, Guid userId, string? sourceRef, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Tells the person, with <paramref name="reason"/>. False when they hold no live grant.</summary>
    Task<bool> RevokeAsync(
        string sourceKey, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default);
}
