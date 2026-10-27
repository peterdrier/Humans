using Humans.Base.Interfaces;

namespace Humans.Grants.Contracts;

/// <summary>
/// A source section filling the one open program with its source key. Expected refusals are returned; dependency
/// failures propagate. Mutations are audited with the actor and the source's reference.
/// </summary>
public interface IGrantWrite : IApplicationService
{
    /// <summary>
    /// Tells the person by email and in-app. Idempotent per person: the result carries an existing live grant's id and
    /// nothing is sent again. <paramref name="sourceRef"/> is the caller's own id for this decision.
    /// </summary>
    Task<GrantIssueResult> IssueAsync(
        string sourceKey, Guid userId, string? sourceRef, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Tells the person, with <paramref name="reason"/>. No live grant is a successful result with Revoked false.</summary>
    Task<GrantRevokeResult> RevokeAsync(
        string sourceKey, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default);
}

public enum GrantWriteRefusal
{
    None,
    NoOpenProgram,
    SourceReferenceTooLong,
    ReasonTooLong,
}

public sealed record GrantIssueResult(Guid GrantId, GrantWriteRefusal Refusal = GrantWriteRefusal.None)
{
    public bool Succeeded => Refusal == GrantWriteRefusal.None;
}

public sealed record GrantRevokeResult(bool Revoked, GrantWriteRefusal Refusal = GrantWriteRefusal.None)
{
    public bool Succeeded => Refusal == GrantWriteRefusal.None;
}
