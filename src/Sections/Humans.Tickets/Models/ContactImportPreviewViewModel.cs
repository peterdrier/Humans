using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Models;

internal sealed record ContactImportPreviewViewModel(
    AttendeeImportPlan Plan,
    IReadOnlyList<AttendeeImportDecisionRow> Rows);

internal sealed record AttendeeImportDecisionRow(
    Guid AttendeeId,
    string? Email,
    string? AttendeeName,
    string VendorTicketId,
    AttendeeImportOutcome Outcome,
    Guid? TargetUserId,
    Guid? UnverifiedRowUserId,
    IReadOnlyList<Guid>? AmbiguousUserIds,
    int GroupSize,
    IReadOnlyList<string>? ObservedNames);
