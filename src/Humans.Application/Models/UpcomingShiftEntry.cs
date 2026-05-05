using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Models;

/// <summary>
/// One row in the agent user-context tail's UpcomingShifts section. A range
/// signup (signups sharing a <c>SignupBlockId</c>) collapses into one entry;
/// a singleton signup (no <c>SignupBlockId</c>) becomes one entry with
/// <see cref="StartDate"/> equal to <see cref="EndDate"/>. <see cref="Key"/>
/// is the <c>SignupBlockId</c> for ranges and the <c>ShiftSignup.Id</c> for
/// singletons; the <c>get_shift_details</c> tool accepts either shape and
/// the dispatcher resolves it.
/// </summary>
public sealed record UpcomingShiftEntry(
    Guid Key,
    string Label,
    LocalDate StartDate,
    LocalDate EndDate,
    int DayCount,
    SignupStatus Status);
