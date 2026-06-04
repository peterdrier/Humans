using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// Computes barrio (camp) shift-obligation compliance and owns the obligation
/// config (the standing "functions" every applicable barrio must staff) plus
/// per-season required-count overrides. Lives in the Camps section. Compliance
/// reads stitch confirmed-signup counts from the Shifts section via
/// <c>IShiftServiceRead</c> and member/role data from this section's own
/// repositories — no EF entity ever leaves the section (only the projection
/// records below).
/// </summary>
public interface IShiftObligationService : IApplicationService
{
    Task<BarrioObligationMatrix> GetComplianceMatrixAsync(int year, CancellationToken ct = default);
    Task<BarrioObligationDetail?> GetBarrioObligationDetailAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<IReadOnlyList<ShiftObligationConfigInfo>> GetFunctionsAsync(CancellationToken ct = default);
    Task UpsertFunctionAsync(ShiftObligationConfigInput input, Guid actorUserId, CancellationToken ct = default);
    Task SetOverrideAsync(Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount, Guid actorUserId, CancellationToken ct = default);

    Task SendReminderAsync(Guid campSeasonId, Guid shiftObligationId, Guid actorUserId, CancellationToken ct = default);
    Task<int> RemindAllNonCompliantAsync(Guid shiftObligationId, Guid actorUserId, CancellationToken ct = default); // returns count emailed
}

public sealed record BarrioObligationMatrix(
    int Year,
    IReadOnlyList<ObligationColumn> Columns,
    IReadOnlyList<BarrioRow> Rows,
    IReadOnlyList<ExemptBarrio> ExemptNobodiesOrg,         // Norg
    IReadOnlyList<OffGridBarrio> OffGridForPower);          // OwnSupply / unclassified, per grid function

public sealed record ObligationColumn(Guid ShiftObligationId, string Name, string TargetUrl, ObligationApplicability Applicability);
public sealed record BarrioRow(Guid CampSeasonId, string BarrioName, string Slug, int ActiveMemberCount, IReadOnlyList<ObligationCell> Cells);
public sealed record ObligationCell(Guid ShiftObligationId, bool Applicable, int Done, int Required, bool UnderMembered);
public sealed record ExemptBarrio(Guid CampSeasonId, string BarrioName, int ActiveMemberCount);
public sealed record OffGridBarrio(Guid CampSeasonId, string BarrioName, string Reason); // "OwnSupply" | "Unclassified"

public sealed record BarrioObligationDetail(
    Guid CampSeasonId, string BarrioName,
    IReadOnlyList<ObligationDetailFunction> Functions);
public sealed record ObligationDetailFunction(
    Guid ShiftObligationId, string Name, int Done, int Required,
    IReadOnlyList<SignedUpMember> SignedUp,                 // count desc
    IReadOnlyList<string> NotYetSignedUpNames);            // optional chase list

public sealed record SignedUpMember(Guid UserId, string Name, int Count);
public sealed record ShiftObligationConfigInfo(Guid Id, ShiftObligationTargetType TargetType, Guid TargetId, string TargetName, string CampRoleSlug, ObligationApplicability Applicability, int DefaultRequiredShiftCount, bool IsActive, int SortOrder);
public sealed record ShiftObligationConfigInput(Guid? Id, ShiftObligationTargetType TargetType, Guid TargetId, string CampRoleSlug, ObligationApplicability Applicability, int DefaultRequiredShiftCount, bool IsActive, int SortOrder);
