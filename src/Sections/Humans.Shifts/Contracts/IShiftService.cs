
using NodaTime;

using Humans.Base.Interfaces;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The members of the internal <c>IShiftManagement</c> that other sections call. Dev-fixture verbs are on
/// <see cref="IShiftSeeding"/>.
/// </summary>
public interface IShiftService : IApplicationService
{
    /// <summary>
    /// Holds <see cref="ShiftsPermissions.Manage"/> or <see cref="ShiftsPermissions.Approve"/> on at least one team:
    /// the gate for the shift pages that aren't about one team.
    /// </summary>
    Task<bool> CanManageOrApproveAnyShiftsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// At most <paramref name="max"/> highest-scoring volunteer-visible rotas in the active event, matched by name.
    /// The caller controls presentation order. Every caller sees the public surface whatever their role.
    /// </summary>
    Task<IReadOnlyList<RotaSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// No visibility filter: the id came from a filtered search and the destination page enforces access.
    /// </summary>
    Task<RotaInfo?> GetRotaAsync(Guid rotaId, CancellationToken cancellationToken = default);


    /// <summary>Includes full shifts.</summary>
    Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query, CancellationToken ct = default);

    /// <summary>
    /// A Pending or Confirmed signup on a current or future shift long enough for cantina meals. Fails closed with
    /// no active event.
    /// </summary>
    Task<bool> HasQualifyingCantinaSignupAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>Users with a Confirmed signup on a shift that day: the Cantina roster.</summary>
    Task<IReadOnlyList<Guid>> GetOnSiteUserIdsAsync(
        Guid eventSettingsId,
        int dayOffset,
        CancellationToken ct = default);
}

/// <summary>
/// Matched on <see cref="Name"/> only. <see cref="Name"/> is an alphabetical tiebreak for the orchestrator, never
/// rendered: <c>&lt;vc:shifts-search-result&gt;</c> fetches the row's display itself from <see cref="RotaId"/>.
/// </summary>
public record RotaSearchHit(
    Guid RotaId,
    string Name,
    int Score);

[Flags]
public enum ShiftBrowseQueryFlags
{
    None = 0,
    IncludeAdminOnly = 1,
    IncludeSignups = 2,
    IncludeHidden = 4
}

public sealed record ShiftBrowseQuery(
    Guid EventSettingsId,
    Guid? DepartmentId = null,
    LocalDate? FromDate = null,
    LocalDate? ToDate = null,
    ShiftBrowseQueryFlags Flags = ShiftBrowseQueryFlags.None,
    Guid? ViewerUserId = null);
