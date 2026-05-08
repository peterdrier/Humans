using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

/// <summary>
/// EF-backed implementation of <see cref="IVolunteerTrackingRepository"/>. The
/// only non-test file that touches <c>DbContext.VolunteerBuildStatuses</c> from
/// the volunteer-tracking migration onward.
/// </summary>
/// <remarks>
/// Uses the Scoped <see cref="HumansDbContext"/> directly (same pattern as
/// <see cref="ShiftSignupRepository"/>) so multi-step mutations on
/// <see cref="VolunteerBuildStatus"/> share one EF change-tracker.
/// </remarks>
public sealed class VolunteerTrackingRepository : IVolunteerTrackingRepository
{
    private readonly HumansDbContext _db;

    public VolunteerTrackingRepository(HumansDbContext db) => _db = db;

    public Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default) =>
        _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

    public Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        throw new NotSupportedException("GetByEventAsync is implemented in a follow-up TDD step.");

    public async Task<VolunteerBuildStatus> UpsertCampSetupAsync(
        Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
        string? notes, Guid? setByUserId, Instant? setAt, CancellationToken ct = default)
    {
        var existing = await _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

        if (existing is null)
        {
            var row = new VolunteerBuildStatus
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EventSettingsId = eventSettingsId,
                BarrioSetupStartDate = barrioSetupStartDate,
                Notes = notes,
                SetByUserId = setByUserId,
                SetAt = setAt,
                BlockedDayOffsets = new(),
            };
            _db.VolunteerBuildStatuses.Add(row);
            await _db.SaveChangesAsync(ct);
            return row;
        }

        existing.BarrioSetupStartDate = barrioSetupStartDate;
        existing.Notes = notes;
        existing.SetByUserId = setByUserId;
        existing.SetAt = setAt;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public Task<IReadOnlyList<int>> ReplaceBlockedDaysAsync(
        Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets,
        CancellationToken ct = default) =>
        throw new NotSupportedException("ReplaceBlockedDaysAsync is implemented in a follow-up TDD step.");

    public Task<bool> SetBlockAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, bool block,
        CancellationToken ct = default) =>
        throw new NotSupportedException("SetBlockAsync is implemented in a follow-up TDD step.");

    public Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        throw new NotSupportedException("GetEligibleBuildSignupsAsync is implemented in a follow-up TDD step.");
}
