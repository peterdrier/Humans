using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A standing obligation for barrios (camps) to staff shifts for a function team or rota.
/// Owned by the Camps section. The <see cref="DefaultRequiredShiftCount"/> applies to every
/// applicable barrio unless a per-season override exists in <see cref="Overrides"/>.
/// </summary>
public class ShiftObligation
{
    public Guid Id { get; init; }

    public ShiftObligationTargetType TargetType { get; set; }

    /// <summary>TeamId or RotaId, interpreted per <see cref="TargetType"/>.</summary>
    public Guid TargetId { get; set; }

    public string CampRoleSlug { get; set; } = string.Empty;

    public ObligationApplicability Applicability { get; set; } = ObligationApplicability.AllBarrios;

    public int DefaultRequiredShiftCount { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant? UpdatedAt { get; set; }

    public ICollection<CampSeasonShiftObligation> Overrides { get; set; } = [];
}
