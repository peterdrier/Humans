using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Email.Services;

/// <summary>
/// Until the <c>email.communication-preferences</c> data migration has run, a user and category with no row
/// here reads the frozen legacy Users row, and writes start from it. Resetting a legacy-backed preference
/// writes a cleared row (<c>OptedOut == null</c>) so the legacy value never comes back.
/// </summary>
internal sealed class CommunicationPreferenceService(
    ICommunicationPreferenceRepository repository,
    IUserService userService,
    IUnsubscribeTokenProvider tokenProvider,
    ITicketService ticketService,
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
        return Effective(await repository.GetByUserIdAsync(info.Id, cancellationToken), LegacyRows(info))
            .Select(ToSnapshot)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, CommunicationPreferenceSnapshot>> GetPreferencesForCategoryAsync(
        MessageCategory category, CancellationToken cancellationToken = default)
    {
        var legacy = (await userService.GetAllRawUserInfosAsync(cancellationToken))
            .SelectMany(LegacyRows)
            .Where(l => l.Category == category)
            .ToList();
        return Effective(await repository.GetByCategoryAsync(category, cancellationToken), legacy)
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

        var ids = userIds.ToHashSet();
        var legacy = (await userService.GetAllRawUserInfosAsync(cancellationToken))
            .Where(u => ids.Contains(u.Id))
            .SelectMany(LegacyRows)
            .Where(l => l.Category == category)
            .ToList();
        return Effective(await repository.GetForUsersAsync(userIds, category, cancellationToken), legacy)
            .Where(r => !r.InboxEnabled)
            .Select(r => r.UserId)
            .ToHashSet();
    }

    public async Task<int> GetCountByCategoryAndStateAsync(
        MessageCategory category, bool optedOut, CancellationToken cancellationToken = default) =>
        (await GetPreferencesForCategoryAsync(category, cancellationToken)).Values.Count(p => p.OptedOut == optedOut);

    /// <summary>The recorded rows: this table's live rows, plus each legacy row no row here shadows.
    /// Cleared rows shadow their legacy row and read as nothing recorded.</summary>
    private static List<CommunicationPreference> Effective(
        IReadOnlyList<CommunicationPreference> rows, IReadOnlyList<CommunicationPreference> legacy)
    {
        var shadowed = rows.Select(r => (r.UserId, r.Category)).ToHashSet();
        return [.. rows.Where(r => r.OptedOut is not null)
            .Concat(legacy.Where(l => !shadowed.Contains((l.UserId, l.Category))))
            .OrderBy(r => r.Category)];
    }

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
        if (category == MessageCategory.Ticketing && IsTicketingLocked(category,
                (await ticketService.GetUserTicketHoldingsAsync(userId, cancellationToken)).HasTicketAttendeeMatch))
        {
            logger.LogWarning("Attempted to change locked Ticketing preference for ticket holder {UserId} — refused", userId);
            return false;
        }

        await UpdatePreferenceCoreAsync(userId, category, optedOut, inboxEnabled, source,
            actorUserId, cancellationToken);
        return true;
    }

    /// <summary>Shared with the preference pages, which render the locked row read-only.</summary>
    internal static bool IsTicketingLocked(MessageCategory category, bool hasTicketOrder) =>
        category == MessageCategory.Ticketing && hasTicketOrder;

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

        if (await LegacyRowAsync(userId, category, cancellationToken) is null)
        {
            await repository.DeleteAsync(userId, category, cancellationToken);
        }
        else
        {
            await repository.UpsertAsync(new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = null,
                UpdatedAt = clock.GetCurrentInstant(),
                UpdateSource = source,
            }, cancellationToken);
        }

        await LogPreferenceChangeAsync(userId,
            $"{category} reset to no preference (null) via {source}", actorUserId: null);

        logger.LogInformation(
            "User {UserId} communication preference {Category} reset to null via {Source}",
            userId, category, source);
    }

    private Task LogPreferenceChangeAsync(Guid userId, string description, Guid? actorUserId) =>
        actorUserId is { } actor
            ? auditLog.LogAsync(AuditAction.CommunicationPreferenceChanged, "User", userId,
                description, actor)
            : auditLog.LogAsync(AuditAction.CommunicationPreferenceChanged, "User", userId,
                description, nameof(CommunicationPreferenceService));

    /// <summary>Keyed by the raw id, without merge resolution: this table's row, else the unshadowed legacy row.</summary>
    private async Task<CommunicationPreference?> CurrentAsync(Guid userId, MessageCategory category, CancellationToken ct) =>
        Effective(
            (await repository.GetByUserIdAsync(userId, ct)).Where(r => r.Category == category).ToList(),
            [.. LegacyRows(await userService.GetRawUserInfoAsync(userId, ct)).Where(l => l.Category == category)])
        .FirstOrDefault();

    private async Task<CommunicationPreference?> LegacyRowAsync(Guid userId, MessageCategory category, CancellationToken ct) =>
        LegacyRows(await userService.GetRawUserInfoAsync(userId, ct)).FirstOrDefault(l => l.Category == category);

    public string GenerateUnsubscribeToken(Guid userId, MessageCategory category) =>
        tokenProvider.GenerateToken(userId, category);

    public (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token) =>
        tokenProvider.ValidateToken(token);

    public Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category) =>
        tokenProvider.GenerateUnsubscribeHeaders(userId, category);

    public string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category) =>
        tokenProvider.GenerateBrowserUnsubscribeUrl(userId, category);

    public async Task<IReadOnlyList<UncopiedPreference>> GetUncopiedLegacyPreferencesAsync(CancellationToken ct = default)
    {
        var present = (await repository.GetAllAsync(ct)).Select(r => (r.UserId, r.Category)).ToHashSet();
        return [.. (await userService.GetAllRawUserInfosAsync(ct))
            .SelectMany(LegacyRows)
            .Where(l => !present.Contains((l.UserId, l.Category)))
            .OrderBy(l => l.UserId).ThenBy(l => l.Category)
            .Select(l => new UncopiedPreference(l.UserId, ToSnapshot(l)))];
    }

    public async Task<int> CopyLegacyPreferencesAsync(CancellationToken ct = default)
    {
        var written = 0;
        foreach (var (userId, p) in await GetUncopiedLegacyPreferencesAsync(ct))
        {
            var row = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = p.Category,
                OptedOut = p.OptedOut,
                InboxEnabled = p.InboxEnabled,
                UpdatedAt = p.UpdatedAt,
                UpdateSource = p.UpdateSource,
                SubscribedAt = p.SubscribedAt,
            };
            if (await repository.TryInsertAsync(row, ct))
                written++;
        }
        return written;
    }

    /// <summary>The only reader of the obsolete legacy slice; it goes when the data migration is retired.
    /// Categories map by name.</summary>
    private static IReadOnlyList<CommunicationPreference> LegacyRows(UserInfo? info)
    {
        if (info is null) return [];
#pragma warning disable HUM_USER_COMMPREFS // read fallback and copy, until the data migration is retired
        var legacy = info.CommunicationPreferences;
#pragma warning restore HUM_USER_COMMPREFS
        return [.. legacy.Select(l => new CommunicationPreference
        {
            Id = l.Id,
            UserId = info.Id,
            Category = Enum.Parse<MessageCategory>(l.Category.ToString()),
            OptedOut = l.OptedOut,
            InboxEnabled = l.InboxEnabled,
            UpdatedAt = l.UpdatedAt,
            UpdateSource = l.UpdateSource,
            SubscribedAt = l.SubscribedAt,
        })];
    }

    /// <summary>
    /// Per category the later <c>UpdatedAt</c> wins; a source-only category moves across; every row touched
    /// is stamped <paramref name="updatedAt"/>. The source keeps a cleared row wherever a legacy row backs it,
    /// so the copy can't bring it back under the merged-away id.
    /// </summary>
    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
    {
        _ = actorUserId;
        var sourceLegacy = LegacyRows(await userService.GetRawUserInfoAsync(sourceUserId, cancellationToken));
        var targetLegacy = LegacyRows(await userService.GetRawUserInfoAsync(targetUserId, cancellationToken));
        var sourceRows = await repository.GetByUserIdAsync(sourceUserId, cancellationToken);
        var targetRows = await repository.GetByUserIdAsync(targetUserId, cancellationToken);
        if (sourceRows.Count == 0 && sourceLegacy.Count == 0)
            return;

        // Target keeps its own rows (cleared ones included), then takes the fold.
        var target = targetRows.ToDictionary(r => r.Category);
        var targetEffective = Effective(targetRows, targetLegacy).ToDictionary(r => r.Category);
        foreach (var src in Effective(sourceRows, sourceLegacy))
        {
            if (targetEffective.TryGetValue(src.Category, out var tgt))
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

        var sourceCleared = sourceLegacy
            .Select(l => new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = sourceUserId,
                Category = l.Category,
                OptedOut = null,
                UpdatedAt = updatedAt,
                UpdateSource = "AccountMerge",
            })
            .ToList();

        await repository.ReplaceForUsersAsync(
            [(sourceUserId, sourceCleared), (targetUserId, target.Values.ToList())], cancellationToken);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = Effective(
            await repository.GetByUserIdAsync(userId, ct),
            LegacyRows(await userService.GetRawUserInfoAsync(userId, ct)));
        return
        [
            new UserDataSlice(CommunicationPreferencesSlice, rows.Select(cp => new
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

    /// <summary>Deletes every row. Users' erasure deletes the frozen legacy rows.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.DeleteAllForUserAsync(userId, ct);
}
