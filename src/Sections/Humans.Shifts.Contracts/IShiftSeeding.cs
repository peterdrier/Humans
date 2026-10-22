
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The dev seeder's verbs, over input records so the boundary never names <c>EventSettings</c> or <c>Rota</c>.
/// </summary>
public interface IShiftSeeding
{
    /// <summary>
    /// Creates the knobs row on demand. The event itself is Settings': seed it first via
    /// <c>Humans.Settings.Contracts.IEventSettingsSeeding</c>.
    /// </summary>
    Task SetShiftBrowsingOpenAsync(Guid eventSettingsId, bool isOpen);

    Task<Guid> CreateRotaAsync(CreateRotaInput input, IReadOnlyList<Guid>? tagIds = null);

    /// <summary>Validates rota ownership, the period's DayOffset range and the volunteer counts.</summary>
    Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input);

    /// <summary>Rotas, shifts and signups go too. Requires the current user to hold the full Admin role.</summary>
    Task<int> DeleteEventAsync(Guid eventSettingsId, CancellationToken cancellationToken = default);
}

/// <summary>The rota fields a seeded fixture sets; everything else takes its schema default.</summary>
public sealed record CreateRotaInput(
    Guid TeamId,
    Guid EventSettingsId,
    string Name,
    ShiftPriority Priority,
    SignupPolicy Policy,
    RotaPeriod Period,
    bool IsVisibleToVolunteers);

public sealed record CreateShiftInput(
    Guid RotaId,
    Guid TeamId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly,
    bool IsAllDay);

public sealed record ShiftMutationResult(bool Succeeded, string Message, Guid? ShiftId = null)
{
    public static ShiftMutationResult Success(string message, Guid shiftId) => new(true, message, shiftId);
    public static ShiftMutationResult Failure(string message) => new(false, message);
}
