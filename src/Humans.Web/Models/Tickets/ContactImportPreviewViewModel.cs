using Humans.Application.Interfaces.Tickets.Dtos;

namespace Humans.Web.Models.Tickets;

public sealed record ContactImportPreviewViewModel(
    AttendeeImportPlan Plan,
    IReadOnlyList<AttendeeImportDecisionRow> Rows);

public sealed record AttendeeImportDecisionRow(
    Guid AttendeeId,
    string? Email,
    string? AttendeeName,
    string VendorTicketId,
    AttendeeImportOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedRowUserId,
    IReadOnlyList<Guid>? AmbiguousUserIds);
