using Humans.Grants.Domain;
using NodaTime;

namespace Humans.Grants.Services;

/// <summary>Revoked wins over expired.</summary>
internal enum GrantState
{
    Active,
    Expired,
    Revoked,
}

/// <summary>
/// <see cref="EventSettingsId"/> is the program's stored event id. When that event is unavailable,
/// <see cref="EventName"/> falls back to the stored year without inferring an event.
/// </summary>
internal sealed record ProgramInfo(
    Guid Id,
    string Name,
    string Entitles,
    string? Description,
    int Year,
    Guid? EventSettingsId,
    string EventName,
    GrantIssuer IssuedBy,
    string? SourceKey,
    LocalDate? GrantsExpireOn,
    GrantProgramStatus Status,
    IReadOnlyList<Guid> ManagerUserIds,
    IReadOnlyList<GrantInfo> Grants)
{
    public bool IsManager(Guid userId) => ManagerUserIds.Contains(userId);

    public int Count(GrantState state, LocalDate today) => Grants.Count(g => g.State(today) == state);
}

internal sealed record GrantInfo(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    string Entitles,
    int Year,
    Guid UserId,
    string? SourceRef,
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

/// <summary>Managers are the full desired set.</summary>
internal sealed record ProgramSave(
    string Name,
    string Entitles,
    string? Description,
    Guid? EventSettingsId,
    GrantIssuer IssuedBy,
    string? SourceKey,
    LocalDate? GrantsExpireOn,
    GrantProgramStatus Status,
    IReadOnlyList<Guid> ManagerUserIds);

/// <summary>The refusal is shown as is: admin pages are English-only.</summary>
internal record GrantMutationResult(string? Refusal = null, bool NotFound = false)
{
    public static readonly GrantMutationResult Ok = new();
}

internal sealed record GrantMutationResult<T>(T? Value = default, string? Refusal = null)
    : GrantMutationResult(Refusal);
