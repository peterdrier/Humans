using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class UserService : IUserService, IUserDataContributor
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<UserService> _logger;

    public UserService(
        HumansDbContext dbContext,
        IClock clock,
        ILogger<UserService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, User>();
        }

        var list = await _dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        return list.ToDictionary(u => u.Id);
    }

    public async Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        return await _dbContext.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);
    }

    public async Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default)
    {
        return await _dbContext.EventParticipations
            .Where(ep => ep.Year == year)
            .ToListAsync(ct);
    }

    public async Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _dbContext.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        var now = _clock.GetCurrentInstant();

        if (existing is not null)
        {
            // Don't override Attended status — it's permanent
            if (existing.Status == ParticipationStatus.Attended)
            {
                _logger.LogWarning(
                    "Cannot declare NotAttending for user {UserId} year {Year} — already Attended",
                    userId, year);
                return existing;
            }

            existing.Status = ParticipationStatus.NotAttending;
            existing.Source = ParticipationSource.UserDeclared;
            existing.DeclaredAt = now;
        }
        else
        {
            existing = new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Year = year,
                Status = ParticipationStatus.NotAttending,
                Source = ParticipationSource.UserDeclared,
                DeclaredAt = now,
            };
            _dbContext.EventParticipations.Add(existing);
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} declared NotAttending for year {Year}",
            userId, year);

        return existing;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _dbContext.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        if (existing is null)
            return false;

        if (existing.Status != ParticipationStatus.NotAttending ||
            existing.Source != ParticipationSource.UserDeclared)
        {
            _logger.LogWarning(
                "Cannot undo NotAttending for user {UserId} year {Year} — status is {Status} from {Source}",
                userId, year, existing.Status, existing.Source);
            return false;
        }

        _dbContext.EventParticipations.Remove(existing);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} undid NotAttending declaration for year {Year}",
            userId, year);

        return true;
    }

    public async Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, CancellationToken ct = default)
    {
        var existing = await _dbContext.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        if (existing is not null)
        {
            // Attended is permanent — never revert
            if (existing.Status == ParticipationStatus.Attended)
                return;

            // Ticket purchase overrides NotAttending
            existing.Status = status;
            existing.Source = ParticipationSource.TicketSync;
            existing.DeclaredAt = null;
        }
        else
        {
            existing = new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Year = year,
                Status = status,
                Source = ParticipationSource.TicketSync,
            };
            _dbContext.EventParticipations.Add(existing);
        }

        // Note: caller is responsible for SaveChangesAsync (batch context)
    }

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _dbContext.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        if (existing is null)
            return;

        // Only remove TicketSync-sourced records
        if (existing.Source != ParticipationSource.TicketSync)
            return;

        // Never remove Attended — it's permanent
        if (existing.Status == ParticipationStatus.Attended)
            return;

        _dbContext.EventParticipations.Remove(existing);

        // Note: caller is responsible for SaveChangesAsync (batch context)
    }

    public async Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default)
    {
        var existing = await _dbContext.EventParticipations
            .Where(ep => ep.Year == year)
            .ToDictionaryAsync(ep => ep.UserId, ct);

        var count = 0;
        foreach (var (userId, status) in entries)
        {
            if (existing.TryGetValue(userId, out var ep))
            {
                // Don't override Attended — permanent
                if (ep.Status == ParticipationStatus.Attended)
                    continue;

                ep.Status = status;
                ep.Source = ParticipationSource.AdminBackfill;
                ep.DeclaredAt = null;
            }
            else
            {
                var newEp = new EventParticipation
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Year = year,
                    Status = status,
                    Source = ParticipationSource.AdminBackfill,
                };
                _dbContext.EventParticipations.Add(newEp);
                existing[userId] = newEp;
            }

            count++;
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backfilled {Count} participation records for year {Year}",
            count, year);

        return count;
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
        {
            return [new UserDataSlice(GdprExportSections.Account, null)];
        }

        var shaped = new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.PreferredLanguage,
            user.GoogleEmail,
            user.UnsubscribedFromCampaigns,
            user.SuppressScheduleChangeEmails,
            ContactSource = user.ContactSource?.ToString(),
            DeletionRequestedAt = user.DeletionRequestedAt.ToInvariantInstantString(),
            DeletionScheduledFor = user.DeletionScheduledFor.ToInvariantInstantString(),
            CreatedAt = user.CreatedAt.ToInvariantInstantString(),
            LastLoginAt = user.LastLoginAt.ToInvariantInstantString()
        };

        return [new UserDataSlice(GdprExportSections.Account, shaped)];
    }

    // ---- Methods added for Profile-section migration (§15 Step 0) ----

    public async Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user?.GoogleEmail is not null)
            return false;

        if (user is null)
            return false;

        user.GoogleEmail = email;
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return;

        user.DisplayName = displayName;
        await _dbContext.SaveChangesAsync(ct);
    }
}
