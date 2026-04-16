using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Service for managing communication opt-in/opt-out preferences.
/// Token generation and URL building are delegated to
/// <see cref="IUnsubscribeTokenProvider"/> (Infrastructure concern).
/// </summary>
public sealed class CommunicationPreferenceService : ICommunicationPreferenceService
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
    private readonly IUnsubscribeTokenProvider _tokenProvider;
    private readonly IClock _clock;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<CommunicationPreferenceService> _logger;

    public CommunicationPreferenceService(
        ICommunicationPreferenceRepository repository,
        IUnsubscribeTokenProvider tokenProvider,
        IClock clock,
        IAuditLogService auditLog,
        ILogger<CommunicationPreferenceService> logger)
    {
        _repository = repository;
        _tokenProvider = tokenProvider;
        _clock = clock;
        _auditLog = auditLog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetPreferencesAsync(
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
            try
            {
                await _repository.AddRangeAsync(toAdd, cancellationToken);
            }
            catch (Exception)
            {
                // Another request already created the defaults — reload
                _repository.ClearChangeTracker();
                existing = await _repository.GetByUserIdAsync(userId, cancellationToken);
            }
        }

        return existing
            .OrderBy(cp => cp.Category)
            .ToList()
            .AsReadOnly();
    }

    public async Task<bool> IsOptedOutAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default)
    {
        if (category.IsAlwaysOn())
            return false;

        var pref = await _repository.GetByUserAndCategoryAsync(userId, category, cancellationToken);
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
            };
            await _repository.AddAsync(pref, cancellationToken);
        }
        else
        {
            if (pref.OptedOut == optedOut)
                return; // idempotent

            pref.OptedOut = optedOut;
            pref.UpdatedAt = now;
            pref.UpdateSource = source;
            await _repository.SaveChangesAsync(cancellationToken);
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
            };
            await _repository.AddAsync(pref, cancellationToken);
        }
        else
        {
            if (pref.OptedOut == optedOut && pref.InboxEnabled == inboxEnabled)
                return; // idempotent

            pref.OptedOut = optedOut;
            pref.InboxEnabled = inboxEnabled;
            pref.UpdatedAt = now;
            pref.UpdateSource = source;
            await _repository.SaveChangesAsync(cancellationToken);
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
}
