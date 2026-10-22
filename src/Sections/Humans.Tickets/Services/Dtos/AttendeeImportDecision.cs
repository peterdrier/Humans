namespace Humans.Tickets.Services.Dtos;

/// <summary>One per distinct email in <see cref="AttendeeImportPlan"/>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><paramref name="AttendeeId"/> is the lead row: the checkbox value, and the row whose email apply rechecks for
/// drift. The rest of the email group is in <paramref name="AdditionalAttendeeIds"/>; apply matches them all.</item>
/// <item><paramref name="TargetUserId"/> is the live user after following any merge tombstone.</item>
/// <item><paramref name="ObservedNames"/> with more than one entry means the buyer used several names; apply still
/// matches one user.</item>
/// </list>
/// </remarks>
internal sealed record AttendeeImportDecision(
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
