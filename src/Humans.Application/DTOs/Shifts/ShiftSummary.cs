namespace Humans.Application.DTOs.Shifts;

/// <summary>
/// Per-user confirmed-shift totals for the Shift Summary view, aggregated in the
/// Shifts repository from confirmed <c>ShiftSignup</c> rows joined to their shift.
/// <see cref="Hours"/> is the sum of <c>Shift.Duration</c> (in hours);
/// <see cref="Count"/> is the number of confirmed signups in the requested scope.
/// </summary>
public sealed record ConfirmedUserShiftTotal(Guid UserId, double Hours, int Count);
