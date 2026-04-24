using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Core profile service. Business logic only — no DbContext, no IMemoryCache.
/// Cache management is handled by the <c>CachingProfileService</c> decorator.
/// Cross-domain reads use owning-section service interfaces.
/// </summary>
public sealed class ProfileService : IProfileService, IUserDataContributor
{
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository;
    private readonly IOnboardingEligibilityQuery _onboardingEligibilityQuery;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IConsentService _consentService;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly ICampaignService _campaignService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(
        IProfileRepository profileRepository,
        IUserService userService,
        IUserEmailRepository userEmailRepository,
        IContactFieldRepository contactFieldRepository,
        ICommunicationPreferenceRepository communicationPreferenceRepository,
        IOnboardingEligibilityQuery onboardingEligibilityQuery,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IMembershipCalculator membershipCalculator,
        IConsentService consentService,
        ITicketQueryService ticketQueryService,
        IApplicationDecisionService applicationDecisionService,
        ICampaignService campaignService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock,
        ILogger<ProfileService> logger)
    {
        _profileRepository = profileRepository;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
        _contactFieldRepository = contactFieldRepository;
        _communicationPreferenceRepository = communicationPreferenceRepository;
        _onboardingEligibilityQuery = onboardingEligibilityQuery;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _membershipCalculator = membershipCalculator;
        _consentService = consentService;
        _ticketQueryService = ticketQueryService;
        _applicationDecisionService = applicationDecisionService;
        _campaignService = campaignService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Domain.Entities.Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // The per-user profile cache (2-min TTL) is managed by CachingProfileService decorator.
        // This inner method always hits the repository.
        return await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
    }

    public async ValueTask<FullProfile?> GetFullProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // GetByUserIdReadOnlyAsync includes VolunteerHistory; GetByUserIdAsync does not.
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        if (profile is null) return null;

        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null) return null;

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var notificationEmail = userEmails.FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified)?.Email ?? user.Email;

        return FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), notificationEmail);
    }

    public async Task<IReadOnlyDictionary<Guid, Domain.Entities.Profile>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        await _profileRepository.GetByUserIdsAsync(userIds, ct);

    public async Task SetMembershipTierAsync(
        Guid userId, MembershipTier tier, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
        {
            _logger.LogWarning(
                "Cannot set membership tier for user {UserId} — no profile exists", userId);
            return;
        }

        profile.MembershipTier = tier;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _profileRepository.UpdateAsync(profile, ct);

        // Store update handled by CachingProfileService decorator
    }

    public async Task<(Domain.Entities.Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, ct);

        // Cross-section → IApplicationDecisionService for latest application
        var applications = await _applicationDecisionService.GetUserApplicationsAsync(userId, ct);
        var latestApplication = applications.Count > 0 ? applications[0] : null;

        return (profile, latestApplication, snapshot.PendingConsentCount);
    }

    public async Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _campaignService.GetActiveOrCompletedGrantsForUserAsync(userId, ct);
    }

    public async Task<(Domain.Entities.Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);

        // Cross-section → IApplicationDecisionService for application status checks
        var applications = await _applicationDecisionService.GetUserApplicationsAsync(userId, ct);
        var isTierLocked = profile is not null && applications.Any(a =>
            a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.Approved);

        MemberApplication? pendingApplication = null;
        var isInitialSetup = profile is null || !profile.IsApproved;
        if (isInitialSetup)
        {
            pendingApplication = applications.FirstOrDefault(a =>
                a.Status == ApplicationStatus.Submitted);
        }

        return (profile, isTierLocked, pendingApplication);
    }

    public Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default) =>
        _profileRepository.GetProfilePictureDataAsync(profileId, ct);

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);

        if (profile is null)
        {
            profile = new Domain.Entities.Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            };
            await _profileRepository.AddAsync(profile, ct);
        }

        profile.BurnerName = request.BurnerName;
        profile.FirstName = request.FirstName;
        profile.LastName = request.LastName;
        profile.City = request.City;
        profile.CountryCode = request.CountryCode;
        profile.Latitude = request.Latitude;
        profile.Longitude = request.Longitude;
        profile.PlaceId = request.PlaceId;
        profile.Bio = request.Bio?.TrimEnd();
        profile.Pronouns = request.Pronouns;
        profile.ContributionInterests = request.ContributionInterests?.TrimEnd();
        profile.BoardNotes = request.BoardNotes?.TrimEnd();
        profile.EmergencyContactName = request.EmergencyContactName;
        profile.EmergencyContactPhone = request.EmergencyContactPhone;
        profile.EmergencyContactRelationship = request.EmergencyContactRelationship;
        profile.NoPriorBurnExperience = request.NoPriorBurnExperience;
        profile.UpdatedAt = now;

        // Parse birthday (stored as LocalDate with year=4 for Feb 29 validity)
        if (request.BirthdayMonth is >= 1 and <= 12 && request.BirthdayDay is >= 1 and <= 31)
        {
            try
            {
                profile.DateOfBirth = new LocalDate(4, request.BirthdayMonth.Value, request.BirthdayDay.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                profile.DateOfBirth = null;
            }
        }
        else
        {
            profile.DateOfBirth = null;
        }

        // Handle profile picture
        if (request.RemoveProfilePicture)
        {
            profile.ProfilePictureData = null;
            profile.ProfilePictureContentType = null;
        }
        else if (request.ProfilePictureData is not null && request.ProfilePictureContentType is not null)
        {
            profile.ProfilePictureData = request.ProfilePictureData;
            profile.ProfilePictureContentType = request.ProfilePictureContentType;
        }

        // Handle tier application during initial setup
        // Cross-section → IApplicationDecisionService for application management
        var isInitialSetup = !profile.IsApproved;
        if (isInitialSetup && request.SelectedTier.HasValue)
        {
            var selectedTier = request.SelectedTier.Value;

            var applications = await _applicationDecisionService.GetUserApplicationsAsync(userId, ct);
            var hasPendingOrApprovedApp = applications.Any(a =>
                a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.Approved);
            if (hasPendingOrApprovedApp)
            {
                selectedTier = profile.MembershipTier;
            }

            if (selectedTier != MembershipTier.Volunteer)
            {
                var existingApp = applications.FirstOrDefault(a =>
                    a.Status == ApplicationStatus.Submitted);

                if (existingApp is not null)
                {
                    // Route update through IApplicationDecisionService (cross-section write)
                    await _applicationDecisionService.UpdateDraftApplicationAsync(
                        existingApp.Id,
                        selectedTier,
                        request.ApplicationMotivation!,
                        request.ApplicationAdditionalInfo,
                        selectedTier == MembershipTier.Asociado ? request.ApplicationSignificantContribution : null,
                        selectedTier == MembershipTier.Asociado ? request.ApplicationRoleUnderstanding : null,
                        ct);
                }
                else if (!hasPendingOrApprovedApp)
                {
                    await _applicationDecisionService.SubmitAsync(
                        userId, selectedTier,
                        request.ApplicationMotivation!,
                        request.ApplicationAdditionalInfo,
                        selectedTier == MembershipTier.Asociado ? request.ApplicationSignificantContribution : null,
                        selectedTier == MembershipTier.Asociado ? request.ApplicationRoleUnderstanding : null,
                        language, ct);
                }
            }
        }

        await _profileRepository.UpdateAsync(profile, ct);

        // Update display name on user (cross-section → IUserService)
        await _userService.UpdateDisplayNameAsync(userId, displayName, ct);

        // Cache invalidation and store update handled by CachingProfileService decorator

        // Check consent eligibility
        await _onboardingEligibilityQuery.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        _logger.LogInformation("User {UserId} updated their profile", userId);

        return profile.Id;
    }

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new OnboardingResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        // 1. Persist deletion-pending fields on User (tracked write via IUserService)
        await _userService.SetDeletionPendingAsync(userId, now, deletionDate, ct);

        // 2. Revoke team memberships and team role assignments
        var endedMemberships = await _teamService.RevokeAllMembershipsAsync(userId, ct);

        // 3. Revoke governance role assignments
        var endedRoles = await _roleAssignmentService.RevokeAllActiveAsync(userId, ct);

        // 4. Audit log
        await _auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            userId);

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}. " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            userId, deletionDate, endedMemberships, endedRoles);

        // 5. Send deletion confirmation email
        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
        var notificationEmail = userEmails.FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified)?.Email
                                ?? user.Email;
        if (notificationEmail is not null)
        {
            await _emailService.SendAccountDeletionRequestedAsync(
                notificationEmail,
                user.DisplayName,
                deletionDate.ToDateTimeUtc(),
                user.PreferredLanguage,
                ct);
        }

        // Store update and cache invalidation handled by CachingProfileService decorator
        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        // Persist clearing of deletion fields (tracked write via IUserService)
        await _userService.ClearDeletionAsync(userId, ct);

        _logger.LogInformation("User {UserId} cancelled account deletion request", userId);

        // Store update and cache invalidation handled by CachingProfileService decorator
        return new OnboardingResult(true);
    }

    public async Task<Instant?> GetEventHoldDateAsync(Guid userId, CancellationToken ct = default)
    {
        // Ticket check is cross-section → ITicketQueryService
        var hasTickets = await _ticketQueryService.HasCurrentEventTicketAsync(userId, ct);
        if (!hasTickets)
            return null;

        return await _ticketQueryService.GetPostEventHoldDateAsync(ct);
    }

    public Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default) =>
        _profileRepository.GetTierCountsAsync(ct);

    public Task<IReadOnlyList<Guid>> GetActiveApprovedUserIdsAsync(CancellationToken ct = default) =>
        _profileRepository.GetActiveApprovedUserIdsAsync(ct);

    public Task<int> GetConsentReviewPendingCountAsync(CancellationToken ct = default) =>
        _profileRepository.GetConsentReviewPendingCountAsync(ct);

    public Task<int> GetNotApprovedAndNotSuspendedCountAsync(CancellationToken ct = default) =>
        _profileRepository.GetNotApprovedAndNotSuspendedCountAsync(ct);

    public async Task<ProfileStatusCounts> GetProfileStatusCountsAsync(CancellationToken ct = default)
    {
        var (approved, suspended, pending) = await _profileRepository.GetStatusCountsAsync(ct);
        return new ProfileStatusCounts(approved, suspended, pending);
    }

    public Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default) =>
        _profileRepository.GetCustomPictureInfoByUserIdsAsync(userIds, ct);

    public async Task<IReadOnlyList<BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default)
    {
        // §15 invariant: the decorator is pure optimization — removing it must
        // leave the app fully functional (just slower). Base service loads the
        // snapshot from the DB on every call.
        var snapshot = await BuildFullProfileSnapshotAsync(ct);
        return GetBirthdayProfilesFromSnapshot(snapshot, month);
    }

    public async Task<IReadOnlyList<LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default)
    {
        var snapshot = await BuildFullProfileSnapshotAsync(ct);
        return GetApprovedProfilesWithLocationFromSnapshot(snapshot);
    }

    public async Task<IReadOnlyList<AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default)
    {
        var snapshot = await BuildFullProfileSnapshotAsync(ct);
        var allUsers = await _userService.GetAllUsersAsync(ct);
        return await GetFilteredHumansFromSnapshotAsync(
            snapshot, search, statusFilter, allUsers, _membershipCalculator, ct);
    }

    /// <summary>
    /// Builds a <see cref="FullProfile"/> snapshot from repositories.
    /// Used by the base <see cref="ProfileService"/> so that every read method
    /// has a DB-backed fallback path. The caching decorator overrides these methods
    /// to serve the same static helpers from its in-memory dict instead.
    /// </summary>
    private async Task<IReadOnlyList<FullProfile>> BuildFullProfileSnapshotAsync(CancellationToken ct)
    {
        var profiles = await _profileRepository.GetAllAsync(ct);
        if (profiles.Count == 0) return [];

        var userIds = profiles.Select(p => p.UserId).ToList();
        var users = await _userService.GetByIdsAsync(userIds, ct);

        var notificationEmails = await _userEmailRepository.GetAllNotificationTargetEmailsAsync(ct);

        var result = new List<FullProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            if (!users.TryGetValue(profile.UserId, out var user))
                continue;

            notificationEmails.TryGetValue(profile.UserId, out var notificationEmail);
            result.Add(FullProfile.Create(profile, user, profile.VolunteerHistory.ToList(), notificationEmail));
        }

        return result;
    }

    /// <summary>
    /// Pure filter for <see cref="GetBirthdayProfilesAsync"/>. Called by both the
    /// base (DB-backed) and decorator (dict-backed) paths with the same output shape.
    /// </summary>
    public static IReadOnlyList<BirthdayProfileInfo> GetBirthdayProfilesFromSnapshot(
        IEnumerable<FullProfile> snapshot, int month)
    {
        return snapshot
            .Where(p => p.IsApproved && !p.IsSuspended && p.BirthdayMonth == month && p.BirthdayDay.HasValue)
            .OrderBy(p => p.BirthdayDay)
            .Select(p => new BirthdayProfileInfo(
                p.UserId, p.DisplayName, p.ProfilePictureUrl,
                p.HasCustomPicture, p.ProfileId, p.BirthdayDay!.Value, p.BirthdayMonth!.Value))
            .ToList();
    }

    /// <summary>
    /// Pure filter for <see cref="GetApprovedProfilesWithLocationAsync"/>. Called by both the
    /// base (DB-backed) and decorator (dict-backed) paths with the same output shape.
    /// </summary>
    public static IReadOnlyList<LocationProfileInfo> GetApprovedProfilesWithLocationFromSnapshot(
        IEnumerable<FullProfile> snapshot)
    {
        return snapshot
            .Where(p => p.IsApproved && !p.IsSuspended && p.Latitude.HasValue && p.Longitude.HasValue)
            .Select(p => new LocationProfileInfo(
                p.UserId, p.DisplayName, p.ProfilePictureUrl,
                p.Latitude!.Value, p.Longitude!.Value, p.City, p.CountryCode))
            .ToList();
    }

    /// <summary>
    /// Snapshot-based filter for <see cref="GetFilteredHumansAsync"/>. Called by both
    /// the base (DB-backed) and decorator (dict-backed) paths. Takes <paramref name="allUsers"/>
    /// and <paramref name="membershipCalculator"/> as parameters because the filter needs to
    /// enumerate profileless users and compute membership partitions — both cross-cutting
    /// concerns the static helper shouldn't own.
    /// </summary>
    public static async Task<IReadOnlyList<AdminHumanRow>> GetFilteredHumansFromSnapshotAsync(
        IEnumerable<FullProfile> snapshot,
        string? search,
        string? statusFilter,
        IReadOnlyList<User> allUsers,
        IMembershipCalculator membershipCalculator,
        CancellationToken ct = default)
    {
        var profilesByUserId = snapshot.ToDictionary(fp => fp.UserId);

        // Build notification email lookup from the provided profile snapshot
        var notificationEmails = new Dictionary<Guid, string>();
        foreach (var user in allUsers)
        {
            if (profilesByUserId.TryGetValue(user.Id, out var fp) && fp.NotificationEmail is not null)
                notificationEmails[user.Id] = fp.NotificationEmail;
        }

        var userList = allUsers.Select(u =>
        {
            profilesByUserId.TryGetValue(u.Id, out var fp);
            return new
            {
                u.Id,
                Email = u.Email ?? string.Empty,
                u.DisplayName,
                u.ProfilePictureUrl,
                CreatedAt = u.CreatedAt.ToDateTimeUtc(),
                LastLoginAt = u.LastLoginAt != null ? u.LastLoginAt.Value.ToDateTimeUtc() : (DateTime?)null,
                HasProfile = fp is not null,
                IsApproved = fp?.IsApproved ?? false,
            };
        }).ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            userList = userList
                .Where(u =>
                    u.Email.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (notificationEmails.TryGetValue(u.Id, out var ne) && ne.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                    u.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var allIds = userList.Select(u => u.Id).ToList();
        var partition = await membershipCalculator.PartitionUsersAsync(allIds, ct);

        HashSet<Guid>? filteredIds = statusFilter switch
        {
            _ when string.Equals(statusFilter, "active", StringComparison.OrdinalIgnoreCase) => partition.Active,
            _ when string.Equals(statusFilter, "missingconsents", StringComparison.OrdinalIgnoreCase) => partition.MissingConsents,
            _ when string.Equals(statusFilter, "pending", StringComparison.OrdinalIgnoreCase) => partition.PendingApproval,
            _ when string.Equals(statusFilter, "suspended", StringComparison.OrdinalIgnoreCase) => partition.Suspended,
            _ when string.Equals(statusFilter, "incomplete", StringComparison.OrdinalIgnoreCase) => partition.IncompleteSignup,
            _ when string.Equals(statusFilter, "deleting", StringComparison.OrdinalIgnoreCase) => partition.PendingDeletion,
            _ => null
        };

        var rows = filteredIds is not null
            ? userList.Where(u => filteredIds.Contains(u.Id)).ToList()
            : userList;

        return rows.Select(r => new AdminHumanRow(
            r.Id,
            notificationEmails.TryGetValue(r.Id, out var primaryEmail) ? primaryEmail : r.Email,
            r.DisplayName,
            r.ProfilePictureUrl,
            r.CreatedAt,
            r.LastLoginAt,
            r.HasProfile,
            r.IsApproved,
            partition.PendingDeletion.Contains(r.Id) ? MembershipStatusLabels.PendingDeletion :
            partition.Suspended.Contains(r.Id) ? MembershipStatusLabels.Suspended :
            partition.PendingApproval.Contains(r.Id) ? MembershipStatusLabels.PendingApproval :
            partition.MissingConsents.Contains(r.Id) ? MembershipStatusLabels.MissingConsents :
            partition.Active.Contains(r.Id) ? MembershipStatusLabels.Active :
            partition.IncompleteSignup.Contains(r.Id) ? MembershipStatusLabels.IncompleteSignup :
            "Unknown"))
        .ToList();
    }

    public async Task<AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return null;

        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);

        // Applications (cross-section → IApplicationDecisionService)
        var applications = await _applicationDecisionService.GetUserApplicationsAsync(userId, ct);

        // UserEmails (within-section → IUserEmailRepository)
        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);

        var consentCount = await _consentService.GetConsentRecordCountAsync(userId, ct);

        // RoleAssignments (cross-section → IRoleAssignmentService)
        var roleAssignments = await _roleAssignmentService.GetByUserIdAsync(userId, ct);

        string? rejectedByName = null;
        if (profile?.RejectedByUserId is not null)
        {
            var rejectedByUser = await _userService.GetByIdAsync(profile.RejectedByUserId.Value, ct);
            rejectedByName = rejectedByUser?.DisplayName;
        }

        return new AdminHumanDetailData(
            user,
            profile,
            applications.OrderByDescending(a => a.SubmittedAt).ToList(),
            consentCount,
            roleAssignments,
            rejectedByName,
            userEmails);
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var pendingRecord = await _userEmailRepository.GetByIdReadOnlyAsync(pendingEmailId, ct);

        if (pendingRecord?.VerificationSentAt.HasValue == true)
        {
            var cooldownEnd = pendingRecord.VerificationSentAt.Value.Plus(Duration.FromMinutes(5));
            if (now < cooldownEnd)
            {
                var minutesUntilResend = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
                return (false, minutesUntilResend, pendingEmailId);
            }
        }

        return (true, 0, null);
    }

    public async Task<IReadOnlyList<UserSearchResult>> SearchApprovedUsersAsync(string query, CancellationToken ct = default)
    {
        var snapshot = await BuildFullProfileSnapshotAsync(ct);
        return SearchApprovedUsersFromSnapshot(snapshot, query);
    }

    public async Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(string query, CancellationToken ct = default)
    {
        var snapshot = await BuildFullProfileSnapshotAsync(ct);
        return SearchHumansFromSnapshot(snapshot, query);
    }

    /// <summary>
    /// Searches all approved, non-suspended profiles from a pre-built <see cref="FullProfile"/>
    /// snapshot for users whose display name or notification email contains <paramref name="query"/>.
    /// Called by <c>CachingProfileService</c> with its private dict snapshot.
    /// </summary>
    public static IReadOnlyList<UserSearchResult> SearchApprovedUsersFromSnapshot(
        IEnumerable<FullProfile> snapshot, string query)
    {
        return snapshot
            .Where(p => p.IsApproved && !p.IsSuspended)
            .Where(p =>
                p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (p.NotificationEmail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(p => new UserSearchResult(p.UserId, p.DisplayName, p.NotificationEmail ?? ""))
            .ToList();
    }

    /// <summary>
    /// Searches all approved, non-suspended profiles from a pre-built <see cref="FullProfile"/>
    /// snapshot for those matching <paramref name="query"/> on any indexed field.
    /// Called by <c>CachingProfileService</c> with its private dict snapshot.
    /// </summary>
    public static IReadOnlyList<HumanSearchResult> SearchHumansFromSnapshot(
        IEnumerable<FullProfile> snapshot, string query)
    {
        var results = new List<HumanSearchResult>();

        foreach (var p in snapshot.Where(p => p.IsApproved && !p.IsSuspended))
        {
            var (matchField, matchSnippet) = DetermineMatchFromCache(p, query);
            if (matchField is null) continue;

            results.Add(new HumanSearchResult(
                p.UserId, p.DisplayName, p.BurnerName, p.City, p.Bio, p.ContributionInterests,
                p.ProfilePictureUrl, p.HasCustomPicture, p.ProfileId, p.UpdatedAtTicks,
                matchField, matchSnippet));
        }

        return results
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
    }

    public async Task SaveCVEntriesAsync(Guid userId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null) return;

        await _profileRepository.ReconcileCVEntriesAsync(profile.Id, entries, ct);
    }

    public Task<IReadOnlyList<ProfileLanguage>> GetProfileLanguagesAsync(
        Guid profileId, CancellationToken ct = default) =>
        _profileRepository.GetLanguagesAsync(profileId, ct);

    public Task SaveProfileLanguagesAsync(Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default) =>
        _profileRepository.ReplaceLanguagesAsync(profileId, languages, ct);

    // ==========================================================================
    // Volunteer Event Profiles — cross-section reads (§15 Step 1 quarantine)
    // ==========================================================================
    // GDPR Export — contributes Profile-section slices
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);

        var contactFields = profile is not null
            ? await _contactFieldRepository.GetByProfileIdReadOnlyAsync(profile.Id, ct)
            : [];

        var userEmails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);

        // VolunteerHistory is eagerly loaded by GetByUserIdReadOnlyAsync
        var volunteerHistory = profile?.VolunteerHistory
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToList() ?? (IReadOnlyList<VolunteerHistoryEntry>)[];

        var profileLanguages = profile is not null
            ? await _profileRepository.GetLanguagesAsync(profile.Id, ct)
            : [];

        var communicationPreferences = await _communicationPreferenceRepository
            .GetByUserIdReadOnlyAsync(userId, ct);

        var profileSlice = profile is null
            ? new UserDataSlice(GdprExportSections.Profile, null)
            : new UserDataSlice(GdprExportSections.Profile, new
            {
                profile.BurnerName,
                profile.FirstName,
                profile.LastName,
                Birthday = profile.DateOfBirth is not null
                    ? $"{profile.DateOfBirth.Value.Month:D2}-{profile.DateOfBirth.Value.Day:D2}"
                    : null,
                profile.City,
                profile.CountryCode,
                profile.Latitude,
                profile.Longitude,
                profile.Bio,
                profile.Pronouns,
                profile.ContributionInterests,
                profile.BoardNotes,
                profile.MembershipTier,
                profile.IsApproved,
                profile.IsSuspended,
                profile.NoPriorBurnExperience,
                ConsentCheckStatus = profile.ConsentCheckStatus?.ToString(),
                ConsentCheckAt = profile.ConsentCheckAt.ToInvariantInstantString(),
                profile.ConsentCheckNotes,
                profile.RejectionReason,
                RejectedAt = profile.RejectedAt.ToInvariantInstantString(),
                profile.EmergencyContactName,
                profile.EmergencyContactPhone,
                profile.EmergencyContactRelationship,
                profile.HasCustomProfilePicture,
                CreatedAt = profile.CreatedAt.ToInvariantInstantString(),
                UpdatedAt = profile.UpdatedAt.ToInvariantInstantString()
            });

        var contactFieldSlice = new UserDataSlice(GdprExportSections.ContactFields, contactFields.Select(cf => new
        {
            cf.FieldType,
            Label = cf.DisplayLabel,
            cf.Value,
            cf.Visibility
        }).ToList());

        var userEmailsSlice = new UserDataSlice(GdprExportSections.UserEmails, userEmails.Select(e => new
        {
            e.Email,
            e.IsVerified,
            e.IsOAuth,
            e.IsNotificationTarget,
            e.Visibility
        }).ToList());

        var volunteerHistorySlice = new UserDataSlice(GdprExportSections.VolunteerHistory, volunteerHistory.Select(vh => new
        {
            Date = vh.Date.ToIsoDateString(),
            vh.EventName,
            vh.Description,
            CreatedAt = vh.CreatedAt.ToInvariantInstantString()
        }).ToList());

        var languagesSlice = new UserDataSlice(GdprExportSections.Languages, profileLanguages.Select(pl => new
        {
            pl.LanguageCode,
            pl.Proficiency
        }).ToList());

        var commPrefsSlice = new UserDataSlice(GdprExportSections.CommunicationPreferences, communicationPreferences.Select(cp => new
        {
            cp.Category,
            cp.OptedOut,
            cp.InboxEnabled,
            UpdatedAt = cp.UpdatedAt.ToInvariantInstantString(),
            cp.UpdateSource
        }).ToList());

        return [
            profileSlice,
            contactFieldSlice,
            userEmailsSlice,
            volunteerHistorySlice,
            languagesSlice,
            commPrefsSlice
        ];
    }

    // ==========================================================================
    // Onboarding-section support methods — profile mutations that OnboardingService
    // delegates here so each section owns its DbSet writes (design-rules §2c).
    // Cache invalidation (FullProfile refresh, nav-badge, notification meter) is
    // handled by the CachingProfileService decorator's wrappers for these methods.
    // ==========================================================================

    public Task<IReadOnlyList<Domain.Entities.Profile>> GetReviewableProfilesAsync(CancellationToken ct = default) =>
        _profileRepository.GetReviewableAsync(ct);

    public Task<int> GetPendingReviewCountAsync(CancellationToken ct = default) =>
        _profileRepository.GetReviewableCountAsync(ct);

    public async Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt is not null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Cleared;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = reviewerId;
        profile.ConsentCheckNotes = notes;
        profile.IsApproved = true;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckCleared, nameof(Domain.Entities.Profile), userId,
            "Consent check cleared",
            reviewerId);

        _logger.LogInformation("Consent check cleared for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = _clock.GetCurrentInstant();

        profile.ConsentCheckStatus = ConsentCheckStatus.Flagged;
        profile.ConsentCheckAt = now;
        profile.ConsentCheckedByUserId = reviewerId;
        profile.ConsentCheckNotes = notes;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.ConsentCheckFlagged, nameof(Domain.Entities.Profile), userId,
            $"Consent check flagged: {notes}",
            reviewerId);

        _logger.LogInformation("Consent check flagged for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        if (profile.RejectedAt is not null)
            return new OnboardingResult(false, "AlreadyRejected");

        var now = _clock.GetCurrentInstant();

        profile.RejectionReason = reason;
        profile.RejectedAt = now;
        profile.RejectedByUserId = reviewerId;
        profile.IsApproved = false;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.SignupRejected, nameof(Domain.Entities.Profile), userId,
            $"Signup rejected{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            reviewerId);

        _logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, reviewerId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = _clock.GetCurrentInstant();

        profile.IsApproved = true;
        profile.UpdatedAt = now;

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.VolunteerApproved, nameof(User), userId,
            "Approved as volunteer",
            adminId);

        _logger.LogInformation("Admin {AdminId} approved human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        profile.IsSuspended = true;
        profile.AdminNotes = notes;
        profile.UpdatedAt = _clock.GetCurrentInstant();

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.MemberSuspended, nameof(User), userId,
            $"Suspended{(string.IsNullOrWhiteSpace(notes) ? "" : $": {notes}")}",
            adminId);

        _logger.LogInformation("Admin {AdminId} suspended human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return new OnboardingResult(false, "NotFound");

        profile.IsSuspended = false;
        profile.UpdatedAt = _clock.GetCurrentInstant();

        await _profileRepository.UpdateAsync(profile, ct);

        await _auditLogService.LogAsync(
            AuditAction.MemberUnsuspended, nameof(User), userId,
            "Unsuspended",
            adminId);

        _logger.LogInformation("Admin {AdminId} unsuspended human {HumanId}", adminId, userId);

        return new OnboardingResult(true);
    }

    public async Task<bool> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _profileRepository.GetByUserIdAsync(userId, ct);
        if (profile is null)
            return false;

        profile.ConsentCheckStatus = ConsentCheckStatus.Pending;
        profile.UpdatedAt = _clock.GetCurrentInstant();
        await _profileRepository.UpdateAsync(profile, ct);

        _logger.LogInformation(
            "User {UserId} has all consents signed, consent check set to Pending", userId);

        return true;
    }

    public Task<bool> AnonymizeExpiredProfileAsync(Guid userId, CancellationToken ct = default) =>
        _profileRepository.AnonymizeForDeletionByUserIdAsync(userId, ct);

    public Task<IReadOnlySet<Guid>> SuspendForMissingConsentAsync(
        IReadOnlyCollection<Guid> userIds,
        Instant now,
        CancellationToken ct = default) =>
        _profileRepository.SuspendManyAsync(userIds, now, ct);

    public Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>>
        DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default) =>
        _profileRepository.DowngradeTierForExpiredAsync(
            currentTier, userIdsToKeep, fallbackTierByUser, now, ct);

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static (string? Field, string? Snippet) DetermineMatchFromCache(FullProfile p, string query)
    {
        if (p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return ("Name", null);
        if (p.BurnerName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Burner Name", null);
        if (p.City?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("City", p.City);
        if (p.ContributionInterests?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Interests", GetSnippet(p.ContributionInterests, query));
        if (p.Bio?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Bio", GetSnippet(p.Bio, query));
        if (p.Pronouns?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return ("Pronouns", p.Pronouns);

        foreach (var v in p.CVEntries)
        {
            if (v.EventName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                v.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Burner CV", v.EventName);
        }

        return (null, null);
    }

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
}
