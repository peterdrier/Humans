using Humans.Shifts.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builds <see cref="ShiftUserSummary"/> / <see cref="ShiftSignupSummary"/>
/// for tests that stub <c>IShiftView</c>.
/// </summary>
/// <remarks>
/// Same reason as <see cref="BurnFixtures"/>: the signup record is positional
/// with nineteen members and is stubbed from six section test projects, so
/// every parameter is defaulted and a test that only cares about the status
/// writes <c>ShiftFixtures.Signup(status: SignupStatus.Pending)</c>. The shape
/// is the boundary projection the Shifts section publishes in place of the
/// <c>ShiftSignup</c> / <c>Shift</c> / <c>Rota</c> rows it used to hand out
/// (nobodies-collective/Humans#866).
/// </remarks>
public static class ShiftFixtures
{
    /// <summary>The date every fixture signup falls on unless overridden.</summary>
    public static readonly LocalDate DefaultDate = new(2026, 8, 1);

    public static ShiftUserSummary UserSummary(
        Guid? userId = null,
        IReadOnlyList<ShiftSignupSummary>? signups = null,
        IReadOnlyList<ShiftTagPreferenceInfo>? tagPreferences = null) =>
        new(userId ?? Guid.NewGuid(), tagPreferences ?? [], signups ?? []);

    public static ShiftSignupSummary Signup(
        Guid? id = null,
        Guid? shiftId = null,
        Guid? signupBlockId = null,
        SignupStatus status = SignupStatus.Confirmed,
        Guid? rotaId = null,
        string rotaName = "Test Rota",
        string? rotaDescription = null,
        string? rotaPracticalInfo = null,
        Guid? teamId = null,
        Guid? eventSettingsId = null,
        LocalDate? date = null,
        ShiftPeriod period = ShiftPeriod.Event,
        bool isAllDay = false,
        LocalTime? windowStart = null,
        double durationHours = 4,
        string? shiftDescription = null,
        Instant? absoluteStart = null,
        Instant? absoluteEnd = null)
    {
        var day = date ?? DefaultDate;
        var start = windowStart ?? new LocalTime(10, 0);
        var end = start.PlusHours((int)durationHours);
        var startInstant = absoluteStart ?? day.At(start).InUtc().ToInstant();
        return new ShiftSignupSummary(
            Id: id ?? Guid.NewGuid(),
            ShiftId: shiftId ?? Guid.NewGuid(),
            SignupBlockId: signupBlockId,
            Status: status,
            RotaId: rotaId ?? Guid.NewGuid(),
            RotaName: rotaName,
            RotaDescription: rotaDescription,
            RotaPracticalInfo: rotaPracticalInfo,
            TeamId: teamId ?? Guid.NewGuid(),
            EventSettingsId: eventSettingsId ?? Guid.NewGuid(),
            Date: day,
            Period: period,
            IsAllDay: isAllDay,
            WindowStart: start,
            WindowEnd: end,
            DurationHours: durationHours,
            ShiftDescription: shiftDescription,
            AbsoluteStart: startInstant,
            AbsoluteEnd: absoluteEnd ?? startInstant.Plus(Duration.FromHours(durationHours)));
    }
}
