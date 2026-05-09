using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Models;

/// <summary>
/// One row in <see cref="AgentUserSnapshot.UpcomingShifts"/>: either a
/// multi-day signup block (rows sharing the same
/// <see cref="Humans.Domain.Entities.ShiftSignup.SignupBlockId"/>) collapsed
/// to a single entry, or a singleton signup. The agent surfaces these
/// summaries in the per-turn user-context tail and calls
/// <c>get_shift_details</c> with <see cref="Key"/> to fetch the full
/// description.
/// </summary>
/// <param name="Key">
/// <see cref="Humans.Domain.Entities.ShiftSignup.SignupBlockId"/> for blocks,
/// <see cref="Humans.Domain.Entities.ShiftSignup.Id"/> for singletons.
/// </param>
/// <param name="Label">Display label — derived from the rota name.</param>
/// <param name="StartDate">Earliest day in the block (== <paramref name="EndDate"/> for singletons).</param>
/// <param name="EndDate">Latest day in the block.</param>
/// <param name="DayCount">Number of distinct days spanned (1 for singletons).</param>
/// <param name="Status">Lifecycle status of the underlying signup(s).</param>
public sealed record UpcomingShiftEntry(
    Guid Key,
    string Label,
    LocalDate StartDate,
    LocalDate EndDate,
    int DayCount,
    SignupStatus Status);
