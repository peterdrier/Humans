using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profiles;

/// <summary>
/// Service for managing communication opt-in/opt-out preferences.
/// Token generation and URL building are delegated to
/// <see cref="IUnsubscribeTokenProvider"/> (Infrastructure concern).
/// </summary>
public sealed class CommunicationPreferenceService : ICommunicationPreferenceService, IUserMerge
{
    private static readonly Dictionary<MessageCategory, bool> DefaultOptedOut = new()
    {
        [MessageCategory.System] = false,
        [MessageCategory.CampaignCodes] = false,
        [MessageCategory.FacilitatedMessages] = false,
        [MessageCategory.Ticketing] = false,
        [MessageCategory.VolunteerUpdates] = false,
        [MessageCategory.TeamUpdates] = false,
        [MessageCategory.Governance] = false,
        [MessageCategory.Marketing] = true,
    };

    private readonly ICommunicationPreferenceRepository _repository;
    private readonly IUserService _userService;
    private readonly IUnsubscribeTokenProvider _tokenProvider;
    private readonly IClock _clock;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<CommunicationPreferenceService> _logger;

    public CommunicationPreferenceService(
        ICommunicationPreferenceRepository repository,
        IUserService userService,
        IUnsubscribeTokenProvider tokenProvider,
        IClock clock,
        IAuditLogService auditLog,
        ILogger<CommunicationPreferenceService> logger)
    {
        _repository = repository;
        _userService = userService;
        _tokenProvider = tokenProvider;
        _clock = clock;
        _auditLog = auditLog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CommunicationPreferenceSnapshot>> GetPreferencesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByUserIdAsync(userId, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var toAdd = new List<CommunicationPreference>();

        foreach (var category in DefaultOptedOut.Keys)
        {
            if (existing.Any(cp => cp.Category == category))
                continue;

            var pref = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = DefaultOptedOut[category],
                UpdatedAt = now,
                UpdateSource = "Default",
            };
            toAdd.Add(pref);
            existing.Add(pref);
        }

        if (toAdd.Count > 0)
        {
            existing = await _repository.AddDefaultsOrReloadAsync(userId, toAdd, cancellationToken);
        }

        return existing
            .OrderBy(cp => cp.Category)
            .Select(ToSnapshot)
            .ToList();
    }

    public async Task<CommunicationPreferenceSnapshot?> GetPreferenceOrNullAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default)
    {
        var info = await _userService.GetUserInfoAsync(userId, cancellationToken);
        var pref = info?.CommunicationPreferences.FirstOrDefault(p => p.Category == category);
        return pref is null ? null : ToSnapshot(pref);
    }

    public async Task<IReadOnlyList<CommunicationPreferenceSnapshot>> GetPreferencesReadOnlyAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await _userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];
        return info.CommunicationPreferences
            .Select(ToSnapshot)
            .ToList();
    }

    private static CommunicationPreferenceSnapshot ToSnapshot(CommunicationPreference preference) =>
        new(
            preference.Category,
            preference.OptedOut,
            preference.InboxEnabled,
            preference.UpdateSource,
            preference.UpdatedAt,
            preference.SubscribedAt);

    private static CommunicationPreferenceSnapshot ToSnapshot(CommunicationPreferenceInfo preference) =>
        new(
            preference.Category,
            preference.OptedOut,
            preference.InboxEnabled,
            preference.UpdateSource,
            preference.UpdatedAt,
            preference.SubscribedAt);

    public async Task<bool> IsOptedOutAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default)
    {
        if (category.IsAlwaysOn())
            return false;

        var info = await _userService.GetUserInfoAsync(userId, cancellationToken);
        var pref = info?.CommunicationPreferences.FirstOrDefault(p => p.Category == category);
        return pref?.OptedOut ?? DefaultOptedOut.GetValueOrDefault(category, false);
    }

    public async Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, string source,
        CancellationToken cancellationToken = default)
    {
        if (category.IsAlwaysOn())
        {
            _logger.LogWarning("Attempted to change always-on preference {Category} for user {UserId} — ignored", category, userId);
            return;
        }

        var now = _clock.GetCurrentInstant();
        var pref = await _repository.GetByUserAndCategoryAsync(userId, category, cancellationToken);

        if (pref is null)
        {
            pref = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = optedOut,
                UpdatedAt = now,
                UpdateSource = source,
                SubscribedAt = optedOut ? null : now,
            };
            await _repository.AddAsync(pref, cancellationToken);
        }
        else
        {
            if (pref.OptedOut == optedOut)
                return; // idempotent

            if (!optedOut && pref.SubscribedAt is null)
                pref.SubscribedAt = now;

            pref.OptedOut = optedOut;
            pref.UpdatedAt = now;
            pref.UpdateSource = source;
            await _repository.UpdateAsync(pref, cancellationToken);
        }

        var description = optedOut
            ? $"{category} opted out via {source}"
            : $"{category} opted in via {source}";

        await _auditLog.LogAsync(
            AuditAction.CommunicationPreferenceChanged,
            "User", userId, description,
            "CommunicationPreferenceService");

        _logger.LogInformation(
            "User {UserId} communication preference {Category} set to OptedOut={OptedOut} via {Source}",
            userId, category, optedOut, source);
    }

    public async Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        CancellationToken cancellationToken = default)
    {
        if (category.IsAlwaysOn())
        {
            _logger.LogWarning("Attempted to change always-on preference {Category} for user {UserId} — ignored", category, userId);
            return;
        }

        var now = _clock.GetCurrentInstant();
        var pref = await _repository.GetByUserAndCategoryAsync(userId, category, cancellationToken);

        if (pref is null)
        {
            pref = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = optedOut,
                InboxEnabled = inboxEnabled,
                UpdatedAt = now,
                UpdateSource = source,
                SubscribedAt = optedOut ? null : now,
            };
            await _repository.AddAsync(pref, cancellationToken);
        }
        else
        {
            if (pref.OptedOut == optedOut && pref.InboxEnabled == inboxEnabled)
                return; // idempotent

            if (!optedOut && pref.SubscribedAt is null)
                pref.SubscribedAt = now;

            pref.OptedOut = optedOut;
            pref.InboxEnabled = inboxEnabled;
            pref.UpdatedAt = now;
            pref.UpdateSource = source;
            await _repository.UpdateAsync(pref, cancellationToken);
        }

        var description = $"{category} set to OptedOut={optedOut}, InboxEnabled={inboxEnabled} via {source}";

        await _auditLog.LogAsync(
            AuditAction.CommunicationPreferenceChanged,
            "User", userId, description,
            "CommunicationPreferenceService");

        _logger.LogInformation(
            "User {UserId} communication preference {Category} set to OptedOut={OptedOut}, InboxEnabled={InboxEnabled} via {Source}",
            userId, category, optedOut, inboxEnabled, source);
    }

    public string GenerateUnsubscribeToken(Guid userId, MessageCategory category) =>
        _tokenProvider.GenerateToken(userId, category);

    public (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token) =>
        _tokenProvider.ValidateToken(token);

    public async Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        !await IsOptedOutAsync(userId, MessageCategory.FacilitatedMessages, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken cancellationToken = default) =>
        await _repository.GetUsersWithInboxDisabledAsync(userIds, category, cancellationToken);

    public async Task<bool> HasAnyPreferencesAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        await _repository.HasAnyAsync(userId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetUsersWithAnyPreferencesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken cancellationToken = default) =>
        await _repository.GetUsersWithAnyPreferencesAsync(userIds, cancellationToken);

    public Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category) =>
        _tokenProvider.GenerateUnsubscribeHeaders(userId, category);

    public string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category) =>
        _tokenProvider.GenerateBrowserUnsubscribeUrl(userId, category);

    public Task<int> GetCountByCategoryAndStateAsync(
        MessageCategory category, bool optedOut, CancellationToken cancellationToken = default) =>
        _repository.GetCountByCategoryAndStateAsync(category, optedOut, cancellationToken);

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
        => _repository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, cancellationToken);
}
