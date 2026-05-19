using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Users;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Catches writes to UserInfo-contributing tables that bypass IUserService
/// (Identity machinery, OAuth callbacks, direct-repo) and invalidates UserInfo.
/// Profile-section writes are handled by CachingUserService directly. See #703.
/// </summary>
public sealed class UserInfoSaveChangesInterceptor(
    IServiceProvider services,
    ILogger<UserInfoSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    // Lazy IServiceProvider — direct ctor injection would close a DI cycle.

    // Snapshot collected pre-commit (Deleted still tracked) and consumed post-commit.
    private readonly ConditionalWeakTable<DbContext, PendingUserInfoInvalidations> _pending = new();

    // Async-only: a sync override would have to fire invalidation as a discarded task and race.

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            var affected = CollectAffected(context);
            if (affected.HasWork)
            {
                _pending.AddOrUpdate(context, affected);
            }
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var affected))
        {
            _pending.Remove(context);
            var refresher = services.GetService<IUserInfoSliceRefresher>();
            if (refresher is not null)
            {
                await ApplyAsync(refresher, affected, cancellationToken);
            }
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private async Task ApplyAsync(
        IUserInfoSliceRefresher refresher,
        PendingUserInfoInvalidations affected,
        CancellationToken ct)
    {
        foreach (var userId in affected.DeletedUserIds)
        {
            await RunAsync("Remove", userId, () => refresher.RemoveAsync(userId, ct), ct);
        }

        foreach (var user in affected.Users.Values.Where(u => !affected.DeletedUserIds.Contains(u.Id)))
        {
            await RunAsync("RefreshUserFields", user.Id, () => refresher.RefreshUserFieldsAsync(user, ct), ct);
        }

        foreach (var userId in affected.UserEmailUserIds.Except(affected.DeletedUserIds))
        {
            await RunAsync("RefreshUserEmails", userId, () => refresher.RefreshUserEmailsAsync(userId, ct), ct);
        }

        foreach (var userId in affected.EventParticipationUserIds.Except(affected.DeletedUserIds))
        {
            await RunAsync("RefreshEventParticipations", userId, () => refresher.RefreshEventParticipationsAsync(userId, ct), ct);
        }

        foreach (var userId in affected.ExternalLoginUserIds.Except(affected.DeletedUserIds))
        {
            await RunAsync("RefreshExternalLogins", userId, () => refresher.RefreshExternalLoginsAsync(userId, ct), ct);
        }

        foreach (var userId in affected.CommunicationPreferenceUserIds.Except(affected.DeletedUserIds))
        {
            await RunAsync("RefreshCommunicationPreferences", userId, () => refresher.RefreshCommunicationPreferencesAsync(userId, ct), ct);
        }
    }

    private async Task RunAsync(string operation, Guid userId, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "UserInfoSaveChangesInterceptor {Operation} failed for {UserId}: {ExType}",
                operation, userId, ex.GetType().Name);
        }
    }

    private static PendingUserInfoInvalidations CollectAffected(DbContext context)
    {
        var affected = new PendingUserInfoInvalidations();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged || entry.State == EntityState.Detached)
                continue;

            switch (entry.Entity)
            {
                case User u:
                    if (entry.State == EntityState.Deleted)
                    {
                        affected.DeletedUserIds.Add(u.Id);
                    }
                    else
                    {
                        affected.Users[u.Id] = CloneUserInfoFields(u);
                    }
                    break;
                case UserEmail ue:
                    affected.UserEmailUserIds.Add(ue.UserId);
                    break;
                case EventParticipation ep:
                    affected.EventParticipationUserIds.Add(ep.UserId);
                    break;
                case IdentityUserLogin<Guid> uil:
                    affected.ExternalLoginUserIds.Add(uil.UserId);
                    break;
                case CommunicationPreference cp:
                    affected.CommunicationPreferenceUserIds.Add(cp.UserId);
                    break;
            }
        }
        return affected;
    }

    private static User CloneUserInfoFields(User user)
    {
#pragma warning disable CS0618 // DisplayName is part of the cached legacy user-column mirror.
        return new User
        {
            Id = user.Id,
            DisplayName = user.DisplayName,
            PreferredLanguage = user.PreferredLanguage,
            ProfilePictureUrl = user.ProfilePictureUrl,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            LastConsentReminderSentAt = user.LastConsentReminderSentAt,
            DeletionRequestedAt = user.DeletionRequestedAt,
            DeletionScheduledFor = user.DeletionScheduledFor,
            DeletionEligibleAfter = user.DeletionEligibleAfter,
            UnsubscribedFromCampaigns = user.UnsubscribedFromCampaigns,
            ICalToken = user.ICalToken,
            SuppressScheduleChangeEmails = user.SuppressScheduleChangeEmails,
            MagicLinkSentAt = user.MagicLinkSentAt,
            GoogleEmailStatus = user.GoogleEmailStatus,
            ContactSource = user.ContactSource,
            ExternalSourceId = user.ExternalSourceId,
            MergedToUserId = user.MergedToUserId,
            MergedAt = user.MergedAt,
            Email = user.IdentityEmailColumn,
        };
#pragma warning restore CS0618
    }

    private sealed class PendingUserInfoInvalidations
    {
        public Dictionary<Guid, User> Users { get; } = new();
        public HashSet<Guid> DeletedUserIds { get; } = [];
        public HashSet<Guid> UserEmailUserIds { get; } = [];
        public HashSet<Guid> EventParticipationUserIds { get; } = [];
        public HashSet<Guid> ExternalLoginUserIds { get; } = [];
        public HashSet<Guid> CommunicationPreferenceUserIds { get; } = [];

        public bool HasWork =>
            Users.Count > 0 ||
            DeletedUserIds.Count > 0 ||
            UserEmailUserIds.Count > 0 ||
            EventParticipationUserIds.Count > 0 ||
            ExternalLoginUserIds.Count > 0 ||
            CommunicationPreferenceUserIds.Count > 0;
    }
}
