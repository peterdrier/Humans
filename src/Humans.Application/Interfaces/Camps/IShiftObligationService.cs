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

    /// <summary>
    /// Creates or updates a function. Returns <see cref="UpsertFunctionResult.DuplicateTarget"/>
    /// when another active/inactive function already owns the same
    /// (<c>TargetType</c>, <c>TargetId</c>) — the unique key — so the caller can show
    /// a friendly validation message instead of surfacing the unique-index violation
    /// as a 500. Returns <see cref="UpsertFunctionResult.NotFound"/> when editing an
    /// id that no longer exists.
    /// </summary>
    Task<UpsertFunctionResult> UpsertFunctionAsync(ShiftObligationConfigInput input, Guid actorUserId, CancellationToken ct = default);
    Task SetOverrideAsync(Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Renders the reminder exactly as it would be sent, for the preview-and-customize
    /// modal. When <paramref name="campSeasonId"/> is supplied, renders that barrio's
    /// real numbers (per-barrio "Remind"). When null (bulk "Remind all"), renders a
    /// representative example for the function. Returns null when the function id is
    /// unknown.
    /// </summary>
    Task<ReminderPreview?> GetReminderPreviewAsync(Guid shiftObligationId, Guid? campSeasonId, CancellationToken ct = default);

    /// <summary>
    /// Sends the per-barrio reminder. When BOTH <paramref name="customSubject"/> and
    /// <paramref name="customBody"/> are non-whitespace, the admin's custom message is
    /// sent verbatim instead of the template (same recipients, link kept as a CTA).
    /// </summary>
    Task SendReminderAsync(Guid campSeasonId, Guid shiftObligationId, Guid actorUserId, string? customSubject = null, string? customBody = null, CancellationToken ct = default);

    /// <summary>
    /// Reminds every non-compliant applicable barrio for the function. When BOTH
    /// <paramref name="customSubject"/> and <paramref name="customBody"/> are
    /// non-whitespace, the admin's custom message is sent verbatim instead of the
    /// template. Returns the count of barrios emailed.
    /// </summary>
    Task<int> RemindAllNonCompliantAsync(Guid shiftObligationId, Guid actorUserId, string? customSubject = null, string? customBody = null, CancellationToken ct = default);
}

public sealed record ReminderPreview(string Subject, string BodyHtml);

public sealed record BarrioObligationMatrix(
    int Year,
    IReadOnlyList<ObligationColumn> Columns,
    IReadOnlyList<BarrioRow> Rows,
    IReadOnlyList<ExemptBarrio> ExemptNobodiesOrg,         // Norg
    IReadOnlyList<OffGridBarrio> OffGridForPower);          // OwnSupply / unclassified, per grid function

public sealed record ObligationColumn(Guid ShiftObligationId, string Name, string TargetUrl, ObligationApplicability Applicability);
// ActiveMemberCount = humans who've actually joined the barrio in-app (Active CampMember rows);
// ExpectedMemberCount = the camp's self-reported size (CampSeasonInfo.MemberCount, the
// "Expected Humans" field). Shift counts only ever include joined (active) humans, so the
// matrix shows joined-vs-expected to explain why a tiny joined count can sit beside a large
// self-reported size.
public sealed record BarrioRow(Guid CampSeasonId, string BarrioName, string Slug, int ActiveMemberCount, int ExpectedMemberCount, IReadOnlyList<ObligationCell> Cells);
public sealed record ObligationCell(Guid ShiftObligationId, bool Applicable, int Done, int Required, bool UnderMembered);
public sealed record ExemptBarrio(Guid CampSeasonId, string BarrioName, int ActiveMemberCount);
public sealed record OffGridBarrio(Guid CampSeasonId, string BarrioName, string Reason); // "OwnSupply" | "Unclassified"

public sealed record BarrioObligationDetail(
    Guid CampSeasonId, string BarrioName,
    int ActiveMemberCount, int ExpectedMemberCount,
    IReadOnlyList<ObligationDetailFunction> Functions);
public sealed record ObligationDetailFunction(
    Guid ShiftObligationId, string Name, int Done, int Required,
    IReadOnlyList<SignedUpMember> SignedUp,                 // count desc
    IReadOnlyList<string> NotYetSignedUpNames);            // optional chase list

public enum UpsertFunctionResult
{
    Saved = 0,
    NotFound = 1,       // edit targeted an id that no longer exists
    DuplicateTarget = 2 // another function already owns this (TargetType, TargetId)
}

public sealed record SignedUpMember(Guid UserId, string Name, int Count);
public sealed record ShiftObligationConfigInfo(Guid Id, ShiftObligationTargetType TargetType, Guid TargetId, string TargetName, string CampRoleSlug, ObligationApplicability Applicability, int DefaultRequiredShiftCount, bool IsActive, int SortOrder);
public sealed record ShiftObligationConfigInput(Guid? Id, ShiftObligationTargetType TargetType, Guid TargetId, string CampRoleSlug, ObligationApplicability Applicability, int DefaultRequiredShiftCount, bool IsActive, int SortOrder);
