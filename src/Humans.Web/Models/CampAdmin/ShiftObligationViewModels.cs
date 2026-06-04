using Humans.Domain.Enums;

namespace Humans.Web.Models.CampAdmin;

// View models for the barrio shift-obligation matrix, per-barrio detail, and the
// functions-config page. These are pure shapes mapped 1:1 from the
// IShiftObligationService projection records in the controller — no computation
// lives here (controllers parse/map/format only; all logic stays in the service).

public sealed class ShiftObligationMatrixViewModel
{
    public required int Year { get; init; }
    public required IReadOnlyList<ShiftObligationColumnViewModel> Columns { get; init; }
    public required IReadOnlyList<ShiftObligationBarrioRowViewModel> Rows { get; init; }
    public required IReadOnlyList<ShiftObligationExemptViewModel> ExemptNobodiesOrg { get; init; }
    public required IReadOnlyList<ShiftObligationOffGridViewModel> OffGridForPower { get; init; }
}

public sealed record ShiftObligationColumnViewModel(
    Guid ShiftObligationId, string Name, string TargetUrl, ObligationApplicability Applicability);

public sealed record ShiftObligationBarrioRowViewModel(
    Guid CampSeasonId, string BarrioName, string Slug, int ActiveMemberCount,
    IReadOnlyList<ShiftObligationCellViewModel> Cells);

public sealed record ShiftObligationCellViewModel(
    Guid ShiftObligationId, bool Applicable, int Done, int Required, bool UnderMembered)
{
    public bool Met => Done >= Required;
}

public sealed record ShiftObligationExemptViewModel(
    Guid CampSeasonId, string BarrioName, int ActiveMemberCount);

public sealed record ShiftObligationOffGridViewModel(
    Guid CampSeasonId, string BarrioName, string Reason);

public sealed class ShiftObligationDetailViewModel
{
    public required Guid CampSeasonId { get; init; }
    public required string BarrioName { get; init; }
    public required IReadOnlyList<ShiftObligationDetailFunctionViewModel> Functions { get; init; }

    // Set on the admin-scoped view so it can render reminder buttons; the
    // barrio-lead view renders read-only (no actions) and leaves this false.
    public bool ShowActions { get; init; }
}

public sealed record ShiftObligationDetailFunctionViewModel(
    Guid ShiftObligationId, string Name, int Done, int Required,
    IReadOnlyList<ShiftObligationSignedUpMemberViewModel> SignedUp,
    IReadOnlyList<string> NotYetSignedUpNames)
{
    public bool Met => Done >= Required;
}

public sealed record ShiftObligationSignedUpMemberViewModel(Guid UserId, string Name, int Count);

public sealed class ShiftObligationFunctionsViewModel
{
    public required IReadOnlyList<ShiftObligationFunctionRowViewModel> Functions { get; init; }
    public ShiftObligationFunctionFormViewModel Form { get; init; } = new();
}

public sealed record ShiftObligationFunctionRowViewModel(
    Guid Id, ShiftObligationTargetType TargetType, Guid TargetId, string TargetName,
    string CampRoleSlug, ObligationApplicability Applicability,
    int DefaultRequiredShiftCount, bool IsActive, int SortOrder);

// Bound from the add/edit form. A null/empty Id means "create"; a present Id edits.
public sealed class ShiftObligationFunctionFormViewModel
{
    public Guid? Id { get; init; }
    public ShiftObligationTargetType TargetType { get; init; }
    public Guid TargetId { get; init; }
    public string CampRoleSlug { get; init; } = string.Empty;
    public ObligationApplicability Applicability { get; init; }
    public int DefaultRequiredShiftCount { get; init; }
    public bool IsActive { get; init; } = true;
    public int SortOrder { get; init; }
}
