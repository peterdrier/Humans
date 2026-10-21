using Humans.Grants.Domain;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>Where a grant stands today: revoked wins, then expiry.</summary>
internal enum GrantState
{
    Active,
    Expired,
    Revoked,
}

/// <summary>A program as the section's own pages see it, with its managers and every grant.</summary>
internal sealed record ProgramInfo(
    Guid Id,
    string Name,
    string Entitles,
    string? Description,
    int Year,
    GrantIssuer IssuedBy,
    string? SourceKey,
    LocalDate? GrantsExpireOn,
    GrantProgramStatus Status,
    Guid CreatedByUserId,
    Instant CreatedAt,
    Instant UpdatedAt,
    IReadOnlyList<Guid> ManagerUserIds,
    IReadOnlyList<GrantInfo> Grants)
{
    public bool IsManager(Guid userId) => ManagerUserIds.Contains(userId);

    public int Count(GrantState state, LocalDate today) => Grants.Count(g => g.State(today) == state);
}

/// <summary>One grant with the program facts its pages need beside it.</summary>
internal sealed record GrantInfo(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    string Entitles,
    int Year,
    Guid UserId,
    string? SourceRef,
    LocalDate? OwnExpiresOn,
    LocalDate? ExpiresOn,
    Instant IssuedAt,
    Guid IssuedByUserId,
    Instant? NotifiedAt,
    Instant? RevokedAt,
    Guid? RevokedByUserId,
    string? RevokeReason)
{
    public GrantState State(LocalDate today) =>
        RevokedAt is not null ? GrantState.Revoked
        : ExpiresOn is { } expires && today > expires ? GrantState.Expired
        : GrantState.Active;
}

/// <summary>What the program form posts. Managers are the full desired set.</summary>
internal sealed record ProgramSave(
    string Name,
    string Entitles,
    string? Description,
    int Year,
    GrantIssuer IssuedBy,
    string? SourceKey,
    LocalDate? GrantsExpireOn,
    GrantProgramStatus Status,
    IReadOnlyList<Guid> ManagerUserIds);

/// <summary>
/// Mutation result: a refusal the admin page shows as is, or success. Admin pages are
/// English-only (memory/code/localization-admin-exempt.md), so the refusal is the message.
/// </summary>
internal record GrantMutationResult(string? Refusal = null)
{
    public static readonly GrantMutationResult Ok = new();
}

/// <summary>Mutation result carrying a value on success.</summary>
internal sealed record GrantMutationResult<T>(T? Value = default, string? Refusal = null)
    : GrantMutationResult(Refusal);
