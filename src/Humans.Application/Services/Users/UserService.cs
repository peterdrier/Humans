using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Users;

/// <summary>
/// Core user service. Business logic only — no DbContext, no IMemoryCache.
/// Persistence flows through <see cref="IUserRepository"/>. After every write
/// that affects a <c>User</c> row, the service re-reads the row and upserts
/// the result into <see cref="IUserStore"/> so in-memory lookups stay current.
/// The store is User-only, so EventParticipation mutations do not touch it.
/// </summary>
public sealed class UserService : IUserService, IUserDataContributor
{
    private readonly IUserRepository _userRepository;
    private readonly IUserStore _userStore;
    private readonly IClock _clock;
    private readonly ILogger<UserService> _logger;

    public UserService(
        IUserRepository userRepository,
        IUserStore userStore,
        IClock clock,
        ILogger<UserService> logger)
    {
        _userRepository = userRepository;
        _userStore = userStore;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // User reads
    // ==========================================================================

    public Task<Domain.Entities.User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        _userRepository.GetByIdReadOnlyAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, Domain.Entities.User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _userRepository.GetByIdsAsync(userIds, ct);

    public Task<IReadOnlyList<Domain.Entities.User>> GetAllUsersAsync(CancellationToken ct = default) =>
        _userRepository.GetAllAsync(ct);

    public Task<Domain.Entities.User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) =>
        _userRepository.GetByEmailOrAlternateAsync(email, ct);

    public Task<IReadOnlyList<Domain.Entities.User>> GetContactUsersAsync(string? search, CancellationToken ct = default) =>
        _userRepository.GetContactUsersAsync(search, ct);

    // ==========================================================================
    // User mutations — each refreshes the store after a successful save
    // ==========================================================================

    public async Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var changed = await _userRepository.TrySetGoogleEmailAsync(userId, email, ct);
        if (changed)
            await RefreshStoreAsync(userId, ct);
        return changed;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        await _userRepository.UpdateDisplayNameAsync(userId, displayName, ct);
        await RefreshStoreAsync(userId, ct);
    }

    public async Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default)
    {
        var changed = await _userRepository.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, ct);
        if (changed)
            await RefreshStoreAsync(userId, ct);
        return changed;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var changed = await _userRepository.ClearDeletionAsync(userId, ct);
        if (changed)
            await RefreshStoreAsync(userId, ct);
        return changed;
    }

    // ==========================================================================
    // EventParticipation — store is User-only, so no Upsert calls here.
    // Save semantics mirror the pre-migration service exactly:
    //   * DeclareNotAttending / UndoNotAttending / BackfillParticipations
    //     all save internally (single-flow commits).
    //   * SetParticipationFromTicketSync / RemoveTicketSyncParticipation
    //     do NOT save — callers batch via the ticket sync flow.
    // ==========================================================================

    public Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _userRepository.GetParticipationAsync(userId, year, ct);

    public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) =>
        _userRepository.GetAllParticipationsForYearAsync(year, ct);

    public async Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _userRepository.GetParticipationAsync(userId, year, ct);
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
            await _userRepository.AddParticipationAsync(existing, ct);
        }

        await _userRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} declared NotAttending for year {Year}",
            userId, year);

        return existing;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _userRepository.GetParticipationAsync(userId, year, ct);

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

        await _userRepository.RemoveParticipationAsync(existing, ct);
        await _userRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} undid NotAttending declaration for year {Year}",
            userId, year);

        return true;
    }

    public async Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, CancellationToken ct = default)
    {
        var existing = await _userRepository.GetParticipationAsync(userId, year, ct);

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
            await _userRepository.AddParticipationAsync(existing, ct);
        }

        // Note: caller is responsible for SaveChangesAsync (batch context)
    }

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var existing = await _userRepository.GetParticipationAsync(userId, year, ct);

        if (existing is null)
            return;

        // Only remove TicketSync-sourced records
        if (existing.Source != ParticipationSource.TicketSync)
            return;

        // Never remove Attended — it's permanent
        if (existing.Status == ParticipationStatus.Attended)
            return;

        await _userRepository.RemoveParticipationAsync(existing, ct);

        // Note: caller is responsible for SaveChangesAsync (batch context)
    }

    public async Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default)
    {
        var existing = await _userRepository.GetParticipationsForYearByUserIdAsync(year, ct);

        // Track rows we've added in this batch so duplicate entries in the
        // input reuse the same instance instead of double-inserting.
        var additions = new Dictionary<Guid, EventParticipation>();

        var count = 0;
        foreach (var (userId, status) in entries)
        {
            if (existing.TryGetValue(userId, out var ep) || additions.TryGetValue(userId, out ep))
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
                await _userRepository.AddParticipationAsync(newEp, ct);
                additions[userId] = newEp;
            }

            count++;
        }

        await _userRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backfilled {Count} participation records for year {Year}",
            count, year);

        return count;
    }

    // ==========================================================================
    // GDPR export — contributes the User-level "Account" slice
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdReadOnlyAsync(userId, ct);

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

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task RefreshStoreAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdReadOnlyAsync(userId, ct);
        if (user is null)
        {
            _userStore.Remove(userId);
            return;
        }

        _userStore.Upsert(CachedUser.Create(user));
    }
}
