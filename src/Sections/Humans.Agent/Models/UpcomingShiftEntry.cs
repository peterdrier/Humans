using NodaTime;

using Humans.Shifts.Contracts;
namespace Humans.Agent.Models;

/// <summary>
/// A multi-day signup block collapsed to one entry, or a singleton signup. <paramref name="Key"/> is the block id for
/// blocks and the signup id for singletons; the agent passes it to <c>get_shift_details</c>.
/// </summary>
internal sealed record UpcomingShiftEntry(
    Guid Key,
    string Label,
    LocalDate StartDate,
    LocalDate EndDate,
    int DayCount,
    SignupStatus Status);
