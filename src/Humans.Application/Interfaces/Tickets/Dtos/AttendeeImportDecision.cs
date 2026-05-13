namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// One per attendee in <see cref="AttendeeImportPlan"/>. Carries the
/// classification plus the data the apply step needs (target user id for
/// attach, unverified row id for delete-then-create, etc).
/// </summary>
/// <param name="AttendeeId">PK of the TicketAttendee row.</param>
/// <param name="Email">Attendee email (case preserved from vendor).</param>
/// <param name="AttendeeName">
/// Resolved display name: LegalName → FirstName+LastName → null fallback.
/// </param>
/// <param name="VendorTicketId">For cross-reference with the vendor dashboard.</param>
/// <param name="Outcome">Classification result.</param>
/// <param name="TargetUserId">
/// For <see cref="AttendeeImportOutcome.AttachVerified"/>: the live user id
/// (post tombstone follow). Null otherwise.
/// </param>
/// <param name="UnverifiedEmailIdToDelete">
/// For <see cref="AttendeeImportOutcome.DeleteUnverifiedThenCreate"/>: the
/// UserEmail row id to delete before provisioning. Null otherwise.
/// </param>
/// <param name="UnverifiedRowUserId">
/// For <see cref="AttendeeImportOutcome.DeleteUnverifiedThenCreate"/>: the
/// owning user id of the unverified row (DeleteEmailAsync requires both).
/// Null otherwise.
/// </param>
/// <param name="AmbiguousUserIds">
/// For <see cref="AttendeeImportOutcome.AmbiguousMultipleVerified"/>: the
/// conflicting user ids, for admin visibility. Null otherwise.
/// </param>
public sealed record AttendeeImportDecision(
    Guid AttendeeId,
    string? Email,
    string? AttendeeName,
    string VendorTicketId,
    AttendeeImportOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedEmailIdToDelete,
    Guid? UnverifiedRowUserId,
    IReadOnlyList<Guid>? AmbiguousUserIds);
