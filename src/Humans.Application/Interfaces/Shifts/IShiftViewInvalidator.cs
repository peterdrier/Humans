using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// One-way cache-staleness signal for <see cref="IShiftView"/>. Implemented by
/// the Singleton caching decorator. Every Shifts-section mutation — and every
/// external cross-section write that affects a shifts row (account deletion,
/// suspension, team-coordinator change) — calls one of these methods after
/// committing its own writes.
/// </summary>
/// <remarks>
/// Mirrors <c>IUserInfoInvalidator</c> (Profiles §15i). The decorator
/// resolves the next read from the inner service; consumers never poke the
/// underlying <c>ConcurrentDictionary</c> directly.
///
/// <para>
/// Issue #720.
/// </para>
/// </remarks>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing shift-view cache flushed cross-section (deletion cascade); remains until ShiftViewService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IShiftViewInvalidator : IInvalidator
{
    /// <summary>
    /// Drops the cached <see cref="DTOs.Shifts.ShiftUserView"/> for a single
    /// user. The next <see cref="IShiftView.GetUser"/> call re-loads from the
    /// inner service.
    /// </summary>
    void InvalidateUser(Guid userId);

    /// <summary>
    /// Drops the cached <see cref="DTOs.Shifts.ShiftRotaView"/> for a single
    /// rota. The next <see cref="IShiftView.GetRota"/> call re-loads from the
    /// inner service.
    /// </summary>
    void InvalidateRota(Guid rotaId);

    /// <summary>
    /// Invalidates everything affected by a shift mutation: the rota that
    /// owns the shift and every user with a signup on it. The decorator
    /// resolves the rota id and signed-up users from its current snapshot
    /// (a best-effort lookup — a cache miss falls through to the next read
    /// which will load fresh data anyway).
    /// </summary>
    void InvalidateShift(Guid shiftId);

    /// <summary>
    /// Drops every cached entry. Used on event-settings flips and other
    /// global resets.
    /// </summary>
    void InvalidateAll();
}
