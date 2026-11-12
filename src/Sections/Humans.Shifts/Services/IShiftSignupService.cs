using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

/// <summary>
/// The signup state machine. Members other sections call live on <see cref="IShiftSignups"/> and
/// <see cref="IShiftSignupSeeding"/>, which this inherits.
/// </summary>
internal interface IShiftSignupService : IShiftSignupSeeding, IApplicationService
{
    /// <summary>No-show entries with review timestamps, without display ordering.</summary>
    Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One atomic save; returns each cancelled signup so the caller can audit them one by one.</summary>
    Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default);

    /// <summary>Re-validates invariants.</summary>
    Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId);

    /// <summary>
    /// Confirmed signups across a range, skipping shifts the user already holds. All share a SignupBlockId for
    /// grouped bail.
    /// </summary>
    Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId);

    /// <summary>Fails before the shift has ended.</summary>
    Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId);

    Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason);

    Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId);

    Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason);

    Task<BailRangeResult> BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null);

    /// <summary>
    /// Each resolved against its own event's calendar; signups whose shift, rota or calendar is missing are dropped.
    /// </summary>
    Task<IReadOnlyList<ShiftSignupSummary>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null, CancellationToken ct = default);

    /// <summary>For admin authorization checks.</summary>
    Task<ShiftSignupTeamProbe?> GetTeamProbeAsync(Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default);

    /// <summary>
    /// Bails an active signup on the shift, else signs up. Dietary info never gates it.
    /// <see cref="ToggleDaySignupOutcome.CanViewRestricted"/> adds dept-coordinator status to
    /// <paramref name="privileged"/>, matching the browse page's privilege check.
    /// </summary>
    Task<ToggleDaySignupOutcome> ToggleDayAsync(
        Guid userId,
        Guid shiftId,
        Guid eventSettingsId,
        bool privileged,
        CancellationToken ct = default);
}

internal sealed record ToggleDaySignupOutcome(
    SignupResult Result,
    bool SignedUp,
    bool CanViewRestricted,
    IReadOnlyList<ShiftSignup> SignupsAfter)
{
    public int ActiveSignupCount => SignupsAfter.Count(s => s.Status is SignupStatus.Pending or SignupStatus.Confirmed);
}

internal sealed record ShiftSignupTeamProbe(
    Guid Id,
    Guid ShiftId,
    Guid TeamId);

internal enum ShiftSignupTeamProbeScope
{
    Signup,
    SignupBlock
}


/// <summary>Range-bail refusal key; null indicates success, including an already-empty block.</summary>
internal sealed record BailRangeResult(string? ErrorKey = null);
