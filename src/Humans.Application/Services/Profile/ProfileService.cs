using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Core profile service. Business logic only — no DbContext, no IMemoryCache.
/// Cache management is handled by the <c>CachingProfileService</c> decorator.
/// Cross-domain reads use owning-section service interfaces.
/// </summary>
public sealed class ProfileService : IProfileService, IUserDataContributor
{
    private readonly IProfileRepository _profileRepository;
    private readonly IProfileStore _store;
    private readonly IUserService _userService;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly IVolunteerHistoryRepository _volunteerHistoryRepository;
    private readonly IContactFieldRepository _contactFieldRepository;
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository;
    private readonly IOnboardingService _onboardingService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IConsentService _consentService;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IClock _clock;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(
        IProfileRepository profileRepository,
        IProfileStore store,
        IUserService userService,
        IUserEmailRepository userEmailRepository,
        IVolunteerHistoryRepository volunteerHistoryRepository,
        IContactFieldRepository contactFieldRepository,
        ICommunicationPreferenceRepository communicationPreferenceRepository,
        IOnboardingService onboardingService,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IMembershipCalculator membershipCalculator,
        IConsentService consentService,
        ITicketQueryService ticketQueryService,
        IApplicationDecisionService applicationDecisionService,
        IClock clock,
        ILogger<ProfileService> logger)
    {
        _profileRepository = profileRepository;
        _store = store;
        _userService = userService;
        _userEmailRepository = userEmailRepository;
        _volunteerHistoryRepository = volunteerHistoryRepository;
        _contactFieldRepository = contactFieldRepository;
        _communicationPreferenceRepository = communicationPreferenceRepository;
        _onboardingService = onboardingService;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _membershipCalculator = membershipCalculator;
        _consentService = consentService;
        _ticketQueryService = ticketQueryService;
        _applicationDecisionService = applicationDecisionService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Domain.Entities.Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // The per-user profile cache (2-min TTL) is managed by CachingProfileService decorator.
        // This inner method always hits the repository.
        return await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
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
        await _profileRepository.UpdateAsync(ct);

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
        // CampaignGrants are owned by the Campaigns section.
        // For now, this is a known cross-section read that will be resolved
        // when the Campaigns section is migrated (§15 Step 1 quarantine).
        // Returning empty until then — this method's consumers need to be
        // re-routed to ICampaignService.
        _logger.LogWarning(
            "GetActiveOrCompletedCampaignGrantsAsync called for user {UserId} but CampaignGrants " +
            "is not yet routed through ICampaignService — returning empty", userId);
        return [];
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
                    // Application updates go through IApplicationDecisionService
                    // For now, the service handles the update internally.
                    // TODO: Add an UpdateApplicationAsync method to IApplicationDecisionService
                    existingApp.Motivation = request.ApplicationMotivation!;
                    existingApp.AdditionalInfo = request.ApplicationAdditionalInfo;
                    existingApp.MembershipTier = selectedTier;
                    existingApp.SignificantContribution = selectedTier == MembershipTier.Asociado
                        ? request.ApplicationSignificantContribution : null;
                    existingApp.RoleUnderstanding = selectedTier == MembershipTier.Asociado
                        ? request.ApplicationRoleUnderstanding : null;
                    existingApp.UpdatedAt = now;
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

        await _profileRepository.UpdateAsync(ct);

        // Update display name on user (cross-section → IUserService)
        await _userService.UpdateDisplayNameAsync(userId, displayName, ct);

        // Cache invalidation and store update handled by CachingProfileService decorator

        // Check consent eligibility
        await _onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        _logger.LogInformation("User {UserId} updated their profile", userId);

        return profile.Id;
    }

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        // Deletion touches User entity (cross-section → IUserService)
        // and team/role data (cross-section → ITeamService/IRoleAssignmentService)
        // For now, keep the core workflow here but route User writes through IUserService.
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new OnboardingResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        // User mutation and team/role revocation are cross-domain writes
        // that still need to go through the UserService/TeamService/RoleService.
        // This is a §15 Step 1 quarantine item — the deletion orchestration
        // should be extracted to a dedicated DeletionService that coordinates
        // across sections. For now, we accept these cross-domain writes.
        user.DeletionRequestedAt = now;
        user.DeletionScheduledFor = deletionDate;

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}",
            user.Id, deletionDate);

        // Store update handled by CachingProfileService decorator
        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        _logger.LogInformation("User {UserId} cancelled account deletion request", userId);

        // Store update handled by CachingProfileService decorator
        return new OnboardingResult(true);
    }

    public async Task<Instant?> GetEventHoldDateAsync(Guid userId, CancellationToken ct = default)
    {
        // Ticket check is cross-section → ITicketQueryService
        var hasTickets = await _ticketQueryService.HasCurrentEventTicketAsync(userId, ct);
        if (!hasTickets)
            return null;

        // EventSettings read is a cross-section dependency on the Tickets section.
        // This is a §15 Step 1 quarantine item — should be extracted to a method
        // on ITicketQueryService that returns the hold date. For now, return a
        // generic 30-day hold as a safe approximation.
        var now = _clock.GetCurrentInstant();
        return now.Plus(Duration.FromDays(30));
    }

    public Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default) =>
        _profileRepository.GetTierCountsAsync(ct);

    public Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default) =>
        _profileRepository.GetCustomPictureInfoByUserIdsAsync(userIds, ct);

    public Task<IReadOnlyList<BirthdayProfileInfo>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default)
    {
        var result = _store.GetAll()
            .Where(p => p.IsApproved && !p.IsSuspended && p.BirthdayMonth == month && p.BirthdayDay.HasValue)
            .OrderBy(p => p.BirthdayDay)
            .Select(p => new BirthdayProfileInfo(p.UserId, p.DisplayName, p.ProfilePictureUrl, p.HasCustomPicture, p.ProfileId, p.BirthdayDay!.Value, p.BirthdayMonth!.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<BirthdayProfileInfo>>(result);
    }

    public Task<IReadOnlyList<LocationProfileInfo>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default)
    {
        var result = _store.GetAll()
            .Where(p => p.IsApproved && !p.IsSuspended && p.Latitude.HasValue && p.Longitude.HasValue)
            .Select(p => new LocationProfileInfo(p.UserId, p.DisplayName, p.ProfilePictureUrl, p.Latitude!.Value, p.Longitude!.Value, p.City, p.CountryCode))
            .ToList();

        return Task.FromResult<IReadOnlyList<LocationProfileInfo>>(result);
    }

    public async Task<IReadOnlyList<AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default)
    {
        // This method has heavy cross-domain reads (Users, UserEmails, MembershipCalculator).
        // The User and UserEmail queries are cross-domain reads that will be fully
        // resolved in §15 Step 1 quarantine. For now, we use IUserService for user data
        // and IUserEmailRepository for notification emails (within-section).

        // Get all users (cross-section → IUserService)
        // At ~500 users, loading all is fine
        var allUserIds = _store.GetAll().Select(p => p.UserId).ToList();
        var users = await _userService.GetByIdsAsync(allUserIds, ct);

        // Get notification emails (within Profile section → IUserEmailRepository)
        var notificationEmails = new Dictionary<Guid, string>();
        foreach (var (userId, user) in users)
        {
            var emails = await _userEmailRepository.GetByUserIdReadOnlyAsync(userId, ct);
            var notifEmail = emails.FirstOrDefault(e => e.IsNotificationTarget && e.IsVerified);
            if (notifEmail is not null)
                notificationEmails[userId] = notifEmail.Email;
        }

        var userList = users.Values.Select(u => new
        {
            u.Id,
            Email = u.Email ?? string.Empty,
            u.DisplayName,
            u.ProfilePictureUrl,
            CreatedAt = u.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = u.LastLoginAt != null ? u.LastLoginAt.Value.ToDateTimeUtc() : (DateTime?)null,
            HasProfile = _store.GetByUserId(u.Id) is not null,
            IsApproved = _store.GetByUserId(u.Id)?.IsApproved ?? false,
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
        var partition = await _membershipCalculator.PartitionUsersAsync(allIds, ct);

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

        // RoleAssignments are cross-domain — pass empty for now (§15 Step 1 quarantine)
        var roleAssignments = new List<RoleAssignment>();

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
            rejectedByName);
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
        // Search through the store + user data (instead of cross-domain DB query)
        var pattern = query.ToUpperInvariant();
        var approved = _store.GetAll()
            .Where(p => p.IsApproved && !p.IsSuspended)
            .ToList();

        var userIds = approved.Select(p => p.UserId).ToList();
        var users = await _userService.GetByIdsAsync(userIds, ct);

        return approved
            .Where(p =>
            {
                if (!users.TryGetValue(p.UserId, out var user)) return false;
                return user.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       (user.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(p =>
            {
                var user = users[p.UserId];
                return new UserSearchResult(p.UserId, user.DisplayName, user.Email ?? "");
            })
            .ToList();
    }

    public Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(string query, CancellationToken ct = default)
    {
        var results = new List<HumanSearchResult>();

        foreach (var p in _store.GetAll().Where(p => p.IsApproved && !p.IsSuspended))
        {
            var (matchField, matchSnippet) = DetermineMatchFromCache(p, query);
            if (matchField is null) continue;

            results.Add(new HumanSearchResult(
                p.UserId, p.DisplayName, p.BurnerName, p.City, p.Bio, p.ContributionInterests,
                p.ProfilePictureUrl, p.HasCustomPicture, p.ProfileId, p.UpdatedAtTicks,
                matchField, matchSnippet));
        }

        var ordered = results
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        return Task.FromResult<IReadOnlyList<HumanSearchResult>>(ordered);
    }

    // ==========================================================================
    // Profile Store (delegates to CachingProfileService decorator)
    // ==========================================================================

    public CachedProfile? GetCachedProfile(Guid userId) =>
        _store.GetByUserId(userId);

    public async Task<CachedProfile?> GetCachedProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var cached = _store.GetByUserId(userId);
        if (cached is not null)
            return cached;

        // Store miss — warm the entry if the user has a profile
        var profile = await _profileRepository.GetByUserIdReadOnlyAsync(userId, ct);
        if (profile is null)
            return null;

        var user = await _userService.GetByIdAsync(userId, ct);
        if (user is null)
            return null;

        var entry = CachedProfile.Create(profile, user);
        _store.Upsert(userId, entry);
        return entry;
    }

    public void UpdateProfileCache(Guid userId, CachedProfile? newValue)
    {
        if (newValue is null)
            _store.Remove(userId);
        else
            _store.Upsert(userId, newValue);
    }

    public Task<IReadOnlyList<ProfileLanguage>> GetProfileLanguagesAsync(
        Guid profileId, CancellationToken ct = default) =>
        _profileRepository.GetLanguagesAsync(profileId, ct);

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

        var volunteerHistory = profile is not null
            ? await _volunteerHistoryRepository.GetByProfileIdReadOnlyAsync(profile.Id, ct)
            : [];

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
    // Helpers
    // ==========================================================================

    private static (string? Field, string? Snippet) DetermineMatchFromCache(CachedProfile p, string query)
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

        foreach (var v in p.VolunteerHistory)
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
