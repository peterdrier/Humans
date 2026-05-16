namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// One per distinct email in <see cref="AttendeeImportPlan"/>. Carries the
/// classification plus the data the apply step needs (target user id for
/// attach, unverified row id for delete-then-create, etc).
/// </summary>
/// <param name="AttendeeId">
/// PK of the lead <c>TicketAttendee</c> row (the checkbox value, and the
/// canonical row whose <c>AttendeeEmail</c> the apply step rechecks for
/// drift). For grouped decisions (one buyer with multiple tickets on the
/// same email), the additional attendees are in <see cref="AdditionalAttendeeIds"/>.
/// </param>
/// <param name="Email">Attendee email (case preserved from vendor's lead row).</param>
/// <param name="AttendeeName">
/// Resolved display name of the lead attendee: LegalName → FirstName+LastName
/// → null fallback. Use <see cref="ObservedNames"/> when surfacing the full
/// set of names observed across grouped attendees.
/// </param>
/// <param name="VendorTicketId">Lead vendor ticket id, for cross-reference with the vendor dashboard.</param>
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
/// <param name="AdditionalAttendeeIds">
/// For email-grouped decisions: the other <c>TicketAttendee.Id</c> values
/// in the same email group beyond <see cref="AttendeeId"/>. Empty when only
/// one attendee shares this email. Apply fans the <c>MatchedUserId</c>
/// update across the lead plus these.
/// </param>
/// <param name="ObservedNames">
/// Distinct resolved display names observed across all attendees in the
/// email group (lead + additional). When length &gt; 1 the operator should
/// be alerted that the buyer used multiple names; the apply outcome still
/// matches a single user to the email.
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
    IReadOnlyList<Guid>? AmbiguousUserIds,
    IReadOnlyList<Guid>? AdditionalAttendeeIds = null,
    IReadOnlyList<string>? ObservedNames = null);
