using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Email.Services;

internal sealed class CommunicationPreferenceService(
    ICommunicationPreferenceRepository repository,
    IUserService userService,
    IUnsubscribeTokenProvider tokenProvider,
    IEnumerable<ICommunicationPreferenceLock> locks,
    IClock clock,
    IAuditLogService auditLog,
    ILogger<CommunicationPreferenceService> logger)
    : ICommunicationPreferenceManagement, IUserDataContributor, IUserMerge
{
    internal const string CommunicationPreferencesSlice = "CommunicationPreferences";

    public async Task<CommunicationPreferenceSnapshot?> GetPreferenceOrNullAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default) =>
        (await GetPreferencesReadOnlyAsync(userId, cancellationToken)).FirstOrDefault(p => p.Category == category);

    public async Task<IReadOnlyList<CommunicationPreferenceSnapshot>> GetPreferencesReadOnlyAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Resolves a merged-away id forward to its survivor.
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];
        return Recorded(await repository.GetByUserIdAsync(info.Id, cancellationToken))
            .Select(ToSnapshot)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, CommunicationPreferenceSnapshot>> GetPreferencesForCategoryAsync(
        MessageCategory category, CancellationToken cancellationToken = default)
    {
        return Recorded(await repository.GetByCategoryAsync(category, cancellationToken))
            .ToDictionary(r => r.UserId, ToSnapshot);
    }

    public async Task<bool> IsOptedOutAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default)
    {
        if (category.IsAlwaysOn())
            return false;

        var pref = await GetPreferenceOrNullAsync(userId, category, cancellationToken);
        return pref?.OptedOut ?? category.DefaultOptedOut();
    }

    public async Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        !await IsOptedOutAsync(userId, MessageCategory.FacilitatedMessages, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        return Recorded(await repository.GetForUsersAsync(userIds, category, cancellationToken))
            .Where(r => !r.InboxEnabled)
            .Select(r => r.UserId)
            .ToHashSet();
    }

    public async Task<int> GetCountByCategoryAndStateAsync(
        MessageCategory category, bool optedOut, CancellationToken cancellationToken = default) =>
        (await GetPreferencesForCategoryAsync(category, cancellationToken)).Values.Count(p => p.OptedOut == optedOut);

    private static List<CommunicationPreference> Recorded(IReadOnlyList<CommunicationPreference> rows) =>
        [.. rows.Where(r => r.OptedOut is not null)];

    private static CommunicationPreferenceSnapshot ToSnapshot(CommunicationPreference p) =>
        new(p.Category, p.OptedOut!.Value, p.InboxEnabled, p.UpdateSource, p.UpdatedAt, p.SubscribedAt);

    public Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, string source,
        CancellationToken cancellationToken = default) =>
        UpdatePreferenceCoreAsync(userId, category, optedOut, inboxEnabled: null, source,
            actorUserId: null, cancellationToken);

    public async Task<bool> UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        if ((await GetLockedCategoriesAsync(locks, userId, cancellationToken)).Contains(category))
        {
            logger.LogWarning("Attempted to change locked preference {Category} for user {UserId} — refused", category, userId);
            return false;
        }

        await UpdatePreferenceCoreAsync(userId, category, optedOut, inboxEnabled, source,
            actorUserId, cancellationToken);
        return true;
    }

    /// <summary>Shared with the preference pages, which render a locked row read-only.</summary>
    internal static async Task<IReadOnlySet<MessageCategory>> GetLockedCategoriesAsync(
        IEnumerable<ICommunicationPreferenceLock> locks, Guid userId, CancellationToken ct)
    {
        var locked = new HashSet<MessageCategory>();
        foreach (var source in locks)
            locked.UnionWith(await source.GetLockedCategoriesAsync(userId, ct));
        return locked;
    }

    private async Task UpdatePreferenceCoreAsync(
        Guid userId, MessageCategory category, bool optedOut, bool? inboxEnabled, string source,
        Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (category.IsAlwaysOn())
        {
            logger.LogWarning("Attempted to change always-on preference {Category} for user {UserId} — ignored", category, userId);
            return;
        }

        var now = clock.GetCurrentInstant();
        var current = await CurrentAsync(userId, category, cancellationToken);
        CommunicationPreference next;
        if (current is null)
        {
            next = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = optedOut,
                InboxEnabled = inboxEnabled ?? true,
                UpdatedAt = now,
                UpdateSource = source,
                SubscribedAt = optedOut ? null : now,
            };
        }
        else
        {
            var nextInbox = inboxEnabled ?? current.InboxEnabled;
            if (current.OptedOut == optedOut && current.InboxEnabled == nextInbox)
                return;

            next = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = optedOut,
                InboxEnabled = nextInbox,
                UpdatedAt = now,
                UpdateSource = source,
                SubscribedAt = !optedOut && current.SubscribedAt is null ? now : current.SubscribedAt,
            };
        }
        await repository.UpsertAsync(next, cancellationToken);

        var description = inboxEnabled.HasValue
            ? $"{category} set to {!optedOut}, Inbox set to {inboxEnabled.Value} via {source}"
            : $"{category} set to {!optedOut} via {source}";
        await LogPreferenceChangeAsync(userId, description, actorUserId);

        if (inboxEnabled.HasValue)
        {
            logger.LogInformation(
                "User {UserId} communication preference {Category} set to OptedOut={OptedOut}, InboxEnabled={InboxEnabled} via {Source}",
                userId, category, optedOut, inboxEnabled.Value, source);
        }
        else
        {
            logger.LogInformation(
                "User {UserId} communication preference {Category} set to OptedOut={OptedOut} via {Source}",
                userId, category, optedOut, source);
        }
    }

    public async Task ResetPreferenceAsync(
        Guid userId, MessageCategory category, string source,
        CancellationToken cancellationToken = default)
    {
        if (category.IsAlwaysOn())
        {
            logger.LogWarning("Attempted to reset always-on preference {Category} for user {UserId} — ignored", category, userId);
            return;
        }

        if (await CurrentAsync(userId, category, cancellationToken) is null)
            return;

        await repository.DeleteAsync(userId, category, cancellationToken);

        await LogPreferenceChangeAsync(userId,
            $"{category} reset to no preference (null) via {source}", actorUserId: null);

        logger.LogInformation(
            "User {UserId} communication preference {Category} reset to null via {Source}",
            userId, category, source);
    }

    private Task LogPreferenceChangeAsync(Guid userId, string description, Guid? actorUserId) =>
        actorUserId is { } actor
            ? auditLog.LogAsync(EmailAuditActions.CommunicationPreferenceChanged, AuditSubjectTypes.User, userId,
                description, actor)
            : auditLog.LogAsync(EmailAuditActions.CommunicationPreferenceChanged, AuditSubjectTypes.User, userId,
                description, nameof(CommunicationPreferenceService));

    /// <summary>Keyed by the raw id, without merge resolution.</summary>
    private async Task<CommunicationPreference?> CurrentAsync(Guid userId, MessageCategory category, CancellationToken ct) =>
        (await repository.GetByUserIdAsync(userId, ct))
            .FirstOrDefault(r => r.Category == category && r.OptedOut is not null);

    public (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token) =>
        tokenProvider.ValidateToken(token);

    public Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category) =>
        tokenProvider.GenerateUnsubscribeHeaders(userId, category);

    public string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category) =>
        tokenProvider.GenerateBrowserUnsubscribeUrl(userId, category);

    /// <summary>
    /// Per category the later <c>UpdatedAt</c> wins; a source-only category moves across; every row touched
    /// is stamped <paramref name="updatedAt"/>. The source's rows are removed.
    /// </summary>
    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
    {
        _ = actorUserId;
        var sourceRows = await repository.GetByUserIdAsync(sourceUserId, cancellationToken);
        var targetRows = await repository.GetByUserIdAsync(targetUserId, cancellationToken);
        if (sourceRows.Count == 0)
            return;

        var target = Recorded(targetRows).ToDictionary(r => r.Category);
        foreach (var src in Recorded(sourceRows))
        {
            if (target.TryGetValue(src.Category, out var tgt))
            {
                var winner = src.UpdatedAt > tgt.UpdatedAt ? src : tgt;
                target[src.Category] = new CommunicationPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = targetUserId,
                    Category = src.Category,
                    OptedOut = winner.OptedOut,
                    InboxEnabled = winner.InboxEnabled,
                    UpdateSource = winner.UpdateSource,
                    SubscribedAt = tgt.SubscribedAt ?? src.SubscribedAt,
                    UpdatedAt = updatedAt,
                };
            }
            else
            {
                target[src.Category] = new CommunicationPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = targetUserId,
                    Category = src.Category,
                    OptedOut = src.OptedOut,
                    InboxEnabled = src.InboxEnabled,
                    UpdateSource = src.UpdateSource,
                    SubscribedAt = src.SubscribedAt,
                    UpdatedAt = updatedAt,
                };
            }
        }

        await repository.ReplaceForUsersAsync(
            [(sourceUserId, []), (targetUserId, target.Values.ToList())], cancellationToken);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = Recorded(await repository.GetByUserIdAsync(userId, ct));
        return
        [
            new UserDataSlice(CommunicationPreferencesSlice, rows.OrderBy(cp => cp.Category).Select(cp => new
            {
                cp.Category,
                cp.OptedOut,
                cp.InboxEnabled,
                SubscribedAt = cp.SubscribedAt.ToIso8601(),
                UpdatedAt = cp.UpdatedAt.ToIso8601(),
                cp.UpdateSource
            }).ToList()),
        ];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CommunicationPreferencesSlice] = null,
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Deletes every row owned by Email.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.DeleteAllForUserAsync(userId, ct);
}
