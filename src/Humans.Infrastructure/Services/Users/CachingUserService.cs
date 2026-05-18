using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Services.Users;

/// <summary>
/// Issue #703. Singleton caching decorator for <see cref="IUserService"/>.
/// Inherits <see cref="TrackedCache{TKey, TValue}"/> for a hit/miss-tracked cache of
/// <see cref="UserInfo"/> entries keyed by userId — the canonical
/// "everything-about-a-person" cache spanning the User and Profile sections
/// (8 contributing tables).
/// </summary>
/// <remarks>
/// <para>
/// Pattern mirrors <c>CachingUserService</c>: dict hits served
/// synchronously, cache miss refills via the inner Scoped
/// <see cref="IUserService"/>, every write through this surface delegates and
/// then refreshes the affected entry. Identity-machinery write paths
/// (<c>UserManager.UpdateAsync</c>, sign-in <c>LastLoginAt</c> bumps, OAuth
/// callback <c>UserEmail</c> writes) are caught by
/// <c>UserInfoSaveChangesInterceptor</c> in Infrastructure, which invokes
/// <see cref="IUserInfoInvalidator.InvalidateAsync"/> for every userId
/// touched by the affected entity set.
/// </para>
/// <para>
/// Registered as Singleton so the dict persists across requests. Scoped
/// dependencies (the inner <see cref="IUserService"/>) are resolved per-call
/// via <see cref="IServiceScopeFactory"/> to avoid the captured-scoped
/// anti-pattern. <see cref="IUserRepository"/>, <see cref="IUserEmailRepository"/>,
/// <see cref="IProfileRepository"/>, and <see cref="IContactFieldRepository"/>
/// are injected directly because they are also Singleton
/// (<c>IDbContextFactory</c>-based).
/// </para>
/// </remarks>
public sealed class CachingUserService :
    TrackedCache<Guid, UserInfo>,
    IUserService,
    IUserMerge,
    IUserInfoInvalidator,
    IUserInfoSliceRefresher
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="IUserService"/>
    /// is registered. Used by the Singleton decorator to resolve the Scoped inner
    /// service per-call without triggering self-resolution on the unkeyed
    /// <see cref="IUserService"/> registration.
    /// </summary>
    public const string InnerServiceKey = "user-inner";

    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingUserService> _logger;

    public CachingUserService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        IProfileRepository profileRepository,
        IContactFieldRepository contactFieldRepository,
        ICommunicationPreferenceRepository communicationPreferenceRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingUserService> logger)
        : base("User.UserInfo", warmOnStartup: true, logger)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _profileRepository = profileRepository;
        _contactFieldRepository = contactFieldRepository;
        _communicationPreferenceRepository = communicationPreferenceRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ==========================================================================
    // UserInfo reads
    // ==========================================================================

    public ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default) =>
        GetAsync(userId, ct);

    /// <summary>
    /// Per-key loader plugged into <see cref="TrackedCache{TKey,TValue}.GetAsync"/>.
    /// Resolves the Scoped inner <see cref="IUserService"/> per call; the base
    /// caches the result via <see cref="TrackedCache{TKey,TValue}.Set"/>.
    /// </summary>
    protected override async ValueTask<UserInfo?> LoadRowAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserService>(InnerServiceKey);
        return await inner.GetUserInfoAsync(userId, ct);
    }

    /// <inheritdoc cref="IUserService.GetAllUserInfosAsync" />
    public async Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        return Values.ToArray();
    }

    /// <inheritdoc cref="IUserService.GetUserInfosAsync" />
    public async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        // Issue #743: warm before the miss-detection loop. Without this, a cold
        // cache turns every requested id into a per-row LoadRowAsync (7 SELECTs
        // each) instead of the single bulk WarmAllAsync.
        await EnsureWarmedAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<Guid, UserInfo>(userIds.Count);
        List<Guid>? misses = null;
        foreach (var id in userIds)
        {
            if (TryGet(id, out var hit))
                result[id] = hit;
            else
                (misses ??= []).Add(id);
        }

        if (misses is not null)
        {
            foreach (var id in misses)
            {
                var info = await LoadRowAsync(id, ct).ConfigureAwait(false);
                if (info is not null)
                {
                    Set(id, info);
                    result[id] = info;
                }
            }
        }

        return result;
    }

    /// <inheritdoc cref="IUserService.SearchUsersAsync" />
    public async Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query, PersonSearchFields fields, int limit = 10, CancellationToken ct = default)
    {
        if (fields == PersonSearchFields.None || string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        await EnsureWarmedAsync(ct).ConfigureAwait(false);

        var includeAdmin = (fields & PersonSearchFields.Admin) != PersonSearchFields.None;

        // Admin-only exact-UserId lookup. Lets an admin paste a UserId from
        // logs / audit trails / URLs and jump straight to that human. Public
        // callers fall through to text matching so they can't enumerate IDs.
        if (includeAdmin && Guid.TryParse(query, out var idGuid))
        {
            if (TryGet(idGuid, out var byId) && byId.Profile is not null && byId.Profile.RejectedAt is null)
            {
                return [
                    new HumanSearchResult(
                        UserId: byId.Id,
                        ProfileId: byId.Profile.Id,
                        BurnerName: byId.BurnerName,
                        ProfilePictureUrl: byId.ProfilePictureUrl,
                        MatchField: "User ID",
                        MatchSnippet: null,
                        MatchedEmail: null)
                ];
            }
            return [];
        }

        var results = new List<HumanSearchResult>();
        foreach (var u in Values)
        {
            // Must have a profile and not be rejected to be searchable.
            if (u.Profile is null) continue;
            if (u.Profile.RejectedAt is not null) continue;

            // Public-only callers never see suspended humans. Admin callers
            // do, because admin search is the primary tool for finding a
            // suspended person to lift suspension etc.
            if (!includeAdmin && u.IsSuspended) continue;

            // Public-only: only approved profiles surface. Admin: pre-approval
            // / consent-pending profiles are valid search targets.
            if (!includeAdmin && !u.Profile.IsApproved) continue;

            var match = TryMatchBuckets(u, query, fields);
            if (match is null) continue;

            results.Add(new HumanSearchResult(
                UserId: u.Id,
                ProfileId: u.Profile.Id,
                BurnerName: u.BurnerName,
                ProfilePictureUrl: u.ProfilePictureUrl,
                MatchField: match.Value.Field,
                MatchSnippet: match.Value.Snippet,
                MatchedEmail: match.Value.MatchedEmail));

            if (results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>
    /// Per-record predicate for <see cref="SearchUsersAsync"/>. Returns the
    /// first bucket that matches <paramref name="query"/>, or null if none do.
    /// Emergency-contact fields are skipped by every branch regardless of
    /// which bits are set.
    /// </summary>
    private static (string Field, string? Snippet, string? MatchedEmail)?
        TryMatchBuckets(UserInfo u, string query, PersonSearchFields fields)
    {
        // Callers guarantee u.Profile is not null (filtered upstream).
        var p = u.Profile!;
        var includeName = (fields & PersonSearchFields.Name) != PersonSearchFields.None;
        var includeBio = (fields & PersonSearchFields.Bio) != PersonSearchFields.None;
        var includeAdmin = (fields & PersonSearchFields.Admin) != PersonSearchFields.None;

        // ── Name bucket ─────────────────────────────────────────────
        if (includeName)
        {
            // BurnerName only — see memory/architecture/burnername-is-the-display-name.md.
            if (!string.IsNullOrEmpty(p.BurnerName) &&
                p.BurnerName.Contains(query, StringComparison.OrdinalIgnoreCase))
                return ("Name", null, null);
        }

        // ── Bio bucket (public long-form + short fields + public ContactFields) ──
        if (includeBio)
        {
            if (p.City?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("City", p.City, null);

            if (p.ContributionInterests?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Interests", GetSnippet(p.ContributionInterests, query), null);

            if (p.Bio?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Bio", GetSnippet(p.Bio, query), null);

            if (p.Pronouns?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Pronouns", p.Pronouns, null);

            foreach (var v in p.VolunteerHistory)
            {
                if (v.EventName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    v.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return ("Burner CV", v.EventName, null);
            }

            foreach (var cf in p.ContactFields)
            {
                if (cf.Visibility != ContactFieldVisibility.AllActiveProfiles) continue;
                if (cf.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return (DisplayLabel(cf), cf.Value, null);
            }
        }

        // ── Admin bucket (verified emails + non-public ContactFields) ───────────
        if (includeAdmin)
        {
            foreach (var email in u.AllVerifiedEmails)
            {
                if (email.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return ("Email", null, email);
            }

            foreach (var cf in p.ContactFields)
            {
                // Public ContactFields were already handled above (when
                // the Bio bit was on). Admin bucket covers the remainder.
                if (cf.Visibility == ContactFieldVisibility.AllActiveProfiles) continue;
                if (cf.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return (DisplayLabel(cf), cf.Value, cf.Value);
            }
        }

        return null;
    }

    private static string DisplayLabel(ContactFieldInfo cf) =>
        !string.IsNullOrWhiteSpace(cf.CustomLabel) ? cf.CustomLabel! : cf.FieldType.ToString();

    private static string GetSnippet(string text, string query, int contextChars = 60)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return text.Length <= contextChars * 2 ? text : text[..(contextChars * 2)] + "...";

        var start = Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index + query.Length + contextChars);
        var snippet = text[start..end];
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }

    /// <summary>
    /// Rebuilds the cache entry for <paramref name="userId"/> directly from
    /// repositories. If the user no longer exists, the entry is removed.
    /// </summary>
    private async Task RefreshEntryAsync(Guid userId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
        {
            DeleteKey(userId);
            return;
        }

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var participations = await _userRepository.GetEventParticipationsByUserIdAsync(userId, ct);
        var loginsMap = await _userRepository.GetExternalLoginsByUserIdsAsync([userId], ct);
        var externalLogins = loginsMap.TryGetValue(userId, out var logins)
            ? logins
            : [];

        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        IReadOnlyList<ContactField> contactFields = [];
        IReadOnlyList<ProfileLanguage> languages = [];
        IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
        if (profile is not null)
        {
            contactFields = await _contactFieldRepository.GetByProfileIdReadOnlyAsync(profile.Id, ct);
            languages = profile.Languages.ToList();
            volunteerHistory = profile.VolunteerHistory.ToList();
        }

        var communicationPreferences = await _communicationPreferenceRepository
            .GetByUserIdReadOnlyAsync(userId, ct);

        Set(userId, UserInfo.Create(
            user, userEmails, participations, externalLogins,
            profile, contactFields, languages, volunteerHistory,
            communicationPreferences));
    }

    /// <summary>
    /// Populates the inherited cache with a <see cref="UserInfo"/> for every
    /// existing user at startup. Bulk-loads each of the 8 contributing tables
    /// once and indexes by userId so per-user materialization is allocation-only.
    /// Trivial at ~500-user scale.
    /// </summary>
    /// <remarks>
    /// Invoked by <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/> via
    /// the <see cref="Microsoft.Extensions.Hosting.IHostedService"/> contract
    /// inherited from <see cref="TrackedCache{TKey,TValue}"/>. The base flips
    /// the warmed flag on success. An empty system (fresh dev DB / new deploy)
    /// is a legitimate warm state — this method simply returns early and the
    /// flag still flips.
    /// </remarks>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var users = await _userRepository.GetAllAsync(ct);
        if (users.Count == 0) return;

        var userIds = users.Select(u => u.Id).ToList();

        var allEmails = await _userEmailRepository.GetAllAsync(ct);
        var emailsByUser = allEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmail>)g.ToList());

        var loginsByUser = await _userRepository.GetExternalLoginsByUserIdsAsync(userIds, ct);

        var profiles = await _profileRepository.GetAllAsync(ct);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var allContactFields = await _contactFieldRepository.GetAllAsync(ct);
        var contactFieldsByProfile = allContactFields
            .GroupBy(c => c.ProfileId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ContactField>)g.ToList());

        var participationsByUser = await _userRepository
            .GetEventParticipationsByUserIdsAsync(userIds, ct);

        var allPreferences = await _communicationPreferenceRepository.GetAllAsync(ct);
        var preferencesByUser = allPreferences
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CommunicationPreference>)g.ToList());

        foreach (var user in users)
        {
            var emails = emailsByUser.TryGetValue(user.Id, out var es)
                ? es : [];
            var logins = loginsByUser.TryGetValue(user.Id, out var ls)
                ? ls : [];
            var participations = participationsByUser.TryGetValue(user.Id, out var ps)
                ? ps : (IReadOnlyList<EventParticipation>)[];

            profileByUser.TryGetValue(user.Id, out var profile);
            IReadOnlyList<ContactField> contactFields = [];
            IReadOnlyList<ProfileLanguage> languages = [];
            IReadOnlyList<VolunteerHistoryEntry> volunteerHistory = [];
            if (profile is not null)
            {
                contactFields = contactFieldsByProfile.TryGetValue(profile.Id, out var cf)
                    ? cf : [];
                languages = profile.Languages.ToList();
                volunteerHistory = profile.VolunteerHistory.ToList();
            }

            var preferences = preferencesByUser.TryGetValue(user.Id, out var pp)
                ? pp : [];

            Set(user.Id, UserInfo.Create(
                user, emails, participations, logins,
                profile, contactFields, languages, volunteerHistory,
                preferences));
        }
    }

    // ==========================================================================
    // IUserInfoInvalidator (cross-section) + IUserInfoSliceRefresher (interceptor)
    // ==========================================================================

    /// <inheritdoc cref="IUserInfoInvalidator.InvalidateAsync" />
    public async Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo invalidate userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        // Warmed cache, row updated: replace in-place via the base primitive
        // (LoadRowAsync → Set, or DeleteKey if the inner returns null).
        await ReplaceAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task RefreshUserFieldsAsync(
        User user,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo refresh user fields userId={UserId} caller={CallerMember} file={CallerFile}",
            user.Id, memberName, Path.GetFileName(filePath));

        if (TryGet(user.Id, out var current))
        {
            Replace(user.Id, WithUserFields(current, user));
            return;
        }

        if (!IsWarmedUp)
        {
            await ReplaceAsync(user.Id, ct).ConfigureAwait(false);
            return;
        }

        Set(user.Id, UserInfo.Create(
            user,
            user.UserEmails.ToList(),
            user.EventParticipations.ToList(),
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []));
    }

    public Task RemoveAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo remove userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));
        DeleteKey(userId);
        return Task.CompletedTask;
    }

    public async Task RefreshUserEmailsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo refresh user-emails userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        if (!TryGet(userId, out var current))
        {
            await ReplaceAsync(userId, ct).ConfigureAwait(false);
            return;
        }

        var rows = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        Replace(userId, current with { UserEmails = ToUserEmailInfos(rows) });
    }

    public async Task RefreshEventParticipationsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo refresh event-participations userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        if (!TryGet(userId, out var current))
        {
            await ReplaceAsync(userId, ct).ConfigureAwait(false);
            return;
        }

        var rows = await _userRepository.GetEventParticipationsByUserIdAsync(userId, ct);
        Replace(userId, current with { EventParticipations = ToEventParticipationInfos(rows) });
    }

    public async Task RefreshExternalLoginsAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo refresh external-logins userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        if (!TryGet(userId, out var current))
        {
            await ReplaceAsync(userId, ct).ConfigureAwait(false);
            return;
        }

        var loginsByUser = await _userRepository.GetExternalLoginsByUserIdsAsync([userId], ct);
        var rows = loginsByUser.TryGetValue(userId, out var logins) ? logins : [];
        Replace(userId, current with { ExternalLogins = ToExternalLoginInfos(rows) });
    }

    public async Task RefreshCommunicationPreferencesAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "")
    {
        _logger.LogDebug(
            "UserInfo refresh communication-preferences userId={UserId} caller={CallerMember} file={CallerFile}",
            userId, memberName, Path.GetFileName(filePath));

        if (!TryGet(userId, out var current))
        {
            await ReplaceAsync(userId, ct).ConfigureAwait(false);
            return;
        }

        var rows = await _communicationPreferenceRepository.GetByUserIdReadOnlyAsync(userId, ct);
        Replace(userId, current with { CommunicationPreferences = ToCommunicationPreferenceInfos(rows) });
    }

    private static IReadOnlyList<UserEmailInfo> ToUserEmailInfos(IEnumerable<UserEmail> rows) =>
        rows
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => new UserEmailInfo(
                e.Id, e.Email, e.IsVerified, e.IsPrimary, e.IsGoogle,
                e.Provider, e.ProviderKey, e.Visibility, e.VerificationSentAt,
                e.CreatedAt, e.UpdatedAt))
            .ToList();

    private static IReadOnlyList<EventParticipationInfo> ToEventParticipationInfos(IEnumerable<EventParticipation> rows) =>
        rows
            .OrderBy(p => p.Year)
            .Select(p => new EventParticipationInfo(
                p.Id, p.Year, p.Status, p.Source, p.DeclaredAt, p.CheckedInAt))
            .ToList();

    private static IReadOnlyList<UserExternalLoginInfo> ToExternalLoginInfos(
        IEnumerable<(string Provider, string ProviderKey)> rows) =>
        rows
            .Select(l => new UserExternalLoginInfo(l.Provider, l.ProviderKey))
            .ToList();

    private static IReadOnlyList<CommunicationPreferenceInfo> ToCommunicationPreferenceInfos(
        IEnumerable<CommunicationPreference> rows) =>
        rows
            .OrderBy(c => c.Category)
            .Select(c => new CommunicationPreferenceInfo(
                c.Id, c.Category, c.OptedOut, c.InboxEnabled,
                c.UpdatedAt, c.UpdateSource, c.SubscribedAt))
            .ToList();

    private static UserInfo WithUserFields(UserInfo current, User user)
    {
#pragma warning disable CS0618 // DisplayName / ProfilePictureUrl are part of the cached legacy user-column mirror.
        return current with
        {
            DisplayName = user.DisplayName,
            PreferredLanguage = user.PreferredLanguage,
            FallbackPictureUrl = user.ProfilePictureUrl,
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
            IdentityEmailColumn = user.IdentityEmailColumn,
        };
#pragma warning restore CS0618
    }

    // ==========================================================================
    // Inner delegation — every other IUserService method passes through and
    // refreshes the affected entry on writes.
    // ==========================================================================

    private async Task<T> WithInnerAsync<T>(Func<IUserService, Task<T>> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInnerAsync(Func<IUserService, Task> work)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserService>(InnerServiceKey);
        await work(inner);
    }

    // GetByIdAsync / GetByIdsAsync — issue #744: serve from the in-memory
    // UserInfo dict for cache hits, falling back to the inner UserService only
    // for ids missing from the cache. The cached payload already carries every
    // User-side column AND the UserEmails collection, so rehydration is
    // mechanical and zero-DB for warm-cache callers. There is no
    // "without emails" variant because there is nothing else the cache could
    // serve — UserInfo is the whole person.
    // Callers consume the returned User as read-only (the repo emits the
    // entity AsNoTracking) so rehydrated instances are safe to share.

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        if (TryGet(userId, out var info))
            return RehydrateUser(info);
        return await WithInnerAsync(inner => inner.GetByIdAsync(userId, ct));
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, User>();

        var result = new Dictionary<Guid, User>(userIds.Count);
        List<Guid>? misses = null;
        foreach (var id in userIds)
        {
            if (TryGet(id, out var info))
                result[id] = RehydrateUser(info);
            else
                (misses ??= []).Add(id);
        }

        if (misses is not null)
        {
            var inner = await WithInnerAsync(svc => svc.GetByIdsAsync(misses, ct));
            foreach (var (id, user) in inner)
                result[id] = user;
        }

        return result;
    }

    /// <summary>
    /// Inverse of <see cref="UserInfo.Create"/> for the User-side columns —
    /// rebuilds the read-only fields the section consumers actually touch
    /// (DisplayName, Email/UserEmails, ProfilePictureUrl, GoogleEmailStatus,
    /// and the rest of the User columns UserInfo carries) plus the full
    /// <see cref="User.UserEmails"/> collection. Identity-machinery fields
    /// (PasswordHash, SecurityStamp, lockout, phone) are not carried in
    /// UserInfo and therefore not rehydrated; callers reading those go
    /// through UserManager / IUserRepository directly, not IUserService.
    /// </summary>
    private static User RehydrateUser(UserInfo info)
    {
#pragma warning disable CS0618 // DisplayName is the legacy column this rehydration mirrors.
        var user = new User
        {
            Id = info.Id,
            DisplayName = info.DisplayName,
            PreferredLanguage = info.PreferredLanguage,
            ProfilePictureUrl = info.ProfilePictureUrl,
            CreatedAt = info.CreatedAt,
            LastLoginAt = info.LastLoginAt,
            LastConsentReminderSentAt = info.LastConsentReminderSentAt,
            DeletionRequestedAt = info.DeletionRequestedAt,
            DeletionScheduledFor = info.DeletionScheduledFor,
            DeletionEligibleAfter = info.DeletionEligibleAfter,
            UnsubscribedFromCampaigns = info.UnsubscribedFromCampaigns,
            ICalToken = info.ICalToken,
            SuppressScheduleChangeEmails = info.SuppressScheduleChangeEmails,
            MagicLinkSentAt = info.MagicLinkSentAt,
            GoogleEmailStatus = info.GoogleEmailStatus,
            ContactSource = info.ContactSource,
            ExternalSourceId = info.ExternalSourceId,
            MergedToUserId = info.MergedToUserId,
            MergedAt = info.MergedAt,
            Email = info.IdentityEmailColumn,
        };
#pragma warning restore CS0618

        foreach (var e in info.UserEmails)
        {
            user.UserEmails.Add(new UserEmail
            {
                Id = e.Id,
                UserId = info.Id,
                Email = e.Email,
                IsVerified = e.IsVerified,
                IsPrimary = e.IsPrimary,
                IsGoogle = e.IsGoogle,
                Provider = e.Provider,
                ProviderKey = e.ProviderKey,
                Visibility = e.Visibility,
            });
        }

        return user;
    }

    public async Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var snapshot = Values;
        var result = new List<EventParticipation>();
        foreach (var u in snapshot)
        {
            foreach (var p in u.EventParticipations)
            {
                if (p.Year != year) continue;
                result.Add(new EventParticipation
                {
                    Id = p.Id,
                    UserId = u.Id,
                    Year = p.Year,
                    Status = p.Status,
                    Source = p.Source,
                    DeclaredAt = p.DeclaredAt,
                    CheckedInAt = p.CheckedInAt,
                });
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<OnsiteUserRow>> GetOnsiteUsersAsync(
        int year, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var result = new List<OnsiteUserRow>();
        foreach (var u in Values)
        {
            var onsiteSince = u.OnsiteSinceForYear(year);
            if (onsiteSince is null) continue;
            result.Add(new OnsiteUserRow(u.Id, u.BurnerName, onsiteSince));
        }
        return result;
    }

    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAllUsersAsync(ct));

    public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByEmailOrAlternateAsync(email, ct));

#pragma warning disable CS0618
    public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetOtherUserIdHavingGoogleEmailAsync(email, excludeUserId, ct));
#pragma warning restore CS0618

    public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAccountsDueForAnonymizationAsync(now, ct));

    public async Task<IReadOnlySet<Guid>> GetMergedSourceIdsAsync(
        Guid targetUserId, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<Guid>();
        foreach (var u in Values)
        {
            if (u.MergedToUserId == targetUserId)
                ids.Add(u.Id);
        }
        return ids;
    }

    public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetUsersWithLoginsButNoEmailsAsync(ct));

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetExternalLoginsByUserIdsAsync(userIds, ct));

    // Writes — delegate to inner, then refresh the affected entry.

    public async Task<bool> TrySetGoogleEmailStatusFromSyncAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.TrySetGoogleEmailStatusFromSyncAsync(userId, status, ct));
        if (result) await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.UpdateDisplayNameAsync(userId, displayName, ct));
        await RefreshEntryAsync(userId, ct);
    }

    public async Task SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.SetPreferredLanguageAsync(userId, preferredLanguage, ct));
        await RefreshEntryAsync(userId, ct);
    }

    public async Task SetICalTokenAsync(Guid userId, Guid token, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.SetICalTokenAsync(userId, token, ct));
        await RefreshEntryAsync(userId, ct);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner =>
            inner.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, eligibleAfter, ct));
        if (updated) await RefreshEntryAsync(userId, ct);
        return updated;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var updated = await WithInnerAsync(inner => inner.ClearDeletionAsync(userId, ct));
        if (updated) await RefreshEntryAsync(userId, ct);
        return updated;
    }

    public Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default) =>
        WithInnerAsync(async inner =>
        {
            await inner.SetLastConsentReminderSentAsync(userId, sentAt, ct);
            await RefreshEntryAsync(userId, ct);
        });

    public async Task<EventParticipation> DeclareNotAttendingAsync(
        Guid userId, int year, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.DeclareNotAttendingAsync(userId, year, ct));
        await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.UndoNotAttendingAsync(userId, year, ct));
        if (result) await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task SetParticipationFromTicketSyncAsync(
        Guid userId, int year, ParticipationStatus status, Instant? checkedInAt, CancellationToken ct = default)
    {
        await WithInnerAsync(inner =>
            inner.SetParticipationFromTicketSyncAsync(userId, year, status, checkedInAt, ct));
        await RefreshEventParticipationsAsync(userId, ct);
    }

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        await WithInnerAsync(inner => inner.RemoveTicketSyncParticipationAsync(userId, year, ct));
        await RefreshEventParticipationsAsync(userId, ct);
    }

    public async Task<int> BackfillParticipationsAsync(
        int year,
        List<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        var count = await WithInnerAsync(inner => inner.BackfillParticipationsAsync(year, entries, ct));
        foreach (var userId in entries.Select(e => e.UserId).Distinct())
            await RefreshEventParticipationsAsync(userId, ct);
        return count;
    }

    public async Task<string?> PurgeOwnDataAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.PurgeOwnDataAsync(userId, ct));
        // Inner already invoked IUserInfoInvalidator on success; we refresh
        // our own dict whether or not the row existed (RefreshEntryAsync removes
        // the entry when the user is gone).
        await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner => inner.ApplyExpiredDeletionAnonymizationAsync(userId, ct));
        if (result is not null) await RefreshEntryAsync(userId, ct);
        return result;
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        var result = await WithInnerAsync(inner =>
            inner.AnonymizeForMergeAsync(sourceUserId, targetUserId, now, ct));
        if (result)
        {
            await RefreshEntryAsync(sourceUserId, ct);
            await RefreshEntryAsync(targetUserId, ct);
        }
        return result;
    }

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var deleted = await WithInnerAsync(inner => inner.DeleteUsersAsync(userIds, ct));
        foreach (var userId in userIds)
        {
            DeleteKey(userId);
        }
        return deleted;
    }

    public async Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var deleted = await WithInnerAsync(inner => inner.DeleteAllExternalLoginsForUserAsync(userId, ct));
        if (deleted > 0) await RefreshEntryAsync(userId, ct);
        return deleted;
    }

    // ==========================================================================
    // IUserMerge — delegate to inner, then refresh both ends.
    // ==========================================================================

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        await WithInnerAsync(inner =>
            inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        await RefreshEntryAsync(mergedFromUserId, ct);
        await RefreshEntryAsync(mergedToUserId, ct);
    }

}
