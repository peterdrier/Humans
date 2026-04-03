using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Services;

public class ProfileService : IProfileService
{
    private readonly HumansDbContext _dbContext;
    private readonly IOnboardingService _onboardingService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(
        HumansDbContext dbContext,
        IOnboardingService onboardingService,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IMembershipCalculator membershipCalculator,
        IClock clock,
        IMemoryCache cache,
        ILogger<ProfileService> logger)
    {
        _dbContext = dbContext;
        _onboardingService = onboardingService;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _membershipCalculator = membershipCalculator;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<(Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, ct);

        var latestApplication = await _dbContext.Applications
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.SubmittedAt)
            .FirstOrDefaultAsync(ct);

        return (profile, latestApplication, snapshot.PendingConsentCount);
    }

    public async Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedCampaignGrantsAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == userId
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed))
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        var isTierLocked = profile is not null && await _dbContext.Applications
            .AnyAsync(a => a.UserId == userId &&
                (a.Status == ApplicationStatus.Submitted ||
                 a.Status == ApplicationStatus.Approved), ct);

        MemberApplication? pendingApplication = null;
        var isInitialSetup = profile is null || !profile.IsApproved;
        if (isInitialSetup)
        {
            pendingApplication = await _dbContext.Applications
                .Where(a => a.UserId == userId &&
                    a.Status == ApplicationStatus.Submitted)
                .FirstOrDefaultAsync(ct);
        }

        return (profile, isTierLocked, pendingApplication);
    }

    public async Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(
        Guid profileId, CancellationToken ct = default)
    {
        var data = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => new { p.ProfilePictureData, p.ProfilePictureContentType })
            .FirstOrDefaultAsync(ct);

        return (data?.ProfilePictureData, data?.ProfilePictureContentType);
    }

    public async Task<Guid> SaveProfileAsync(
        Guid userId, string displayName, ProfileSaveRequest request, string language,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
        {
            profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Profiles.Add(profile);
            await _dbContext.SaveChangesAsync(ct);
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

        // Parse birthday (stored as LocalDate with year=4, a leap year, so Feb 29 is valid)
        if (request.BirthdayMonth is >= 1 and <= 12 && request.BirthdayDay is >= 1 and <= 31)
        {
            try
            {
                profile.DateOfBirth = new LocalDate(4, request.BirthdayMonth.Value, request.BirthdayDay.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Invalid month/day combinations from form input are treated as "no birthday provided".
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
        var isInitialSetup = !profile.IsApproved;
        if (isInitialSetup && request.SelectedTier.HasValue)
        {
            var selectedTier = request.SelectedTier.Value;

            // Server-side enforcement: don't allow tier changes if application exists
            var hasPendingOrApprovedApp = await _dbContext.Applications
                .AnyAsync(a => a.UserId == userId &&
                    (a.Status == ApplicationStatus.Submitted ||
                     a.Status == ApplicationStatus.Approved), ct);
            if (hasPendingOrApprovedApp)
            {
                selectedTier = profile.MembershipTier;
            }

            if (selectedTier != MembershipTier.Volunteer)
            {
                var existingApp = await _dbContext.Applications
                    .FirstOrDefaultAsync(a => a.UserId == userId &&
                        a.Status == ApplicationStatus.Submitted, ct);

                if (existingApp is not null)
                {
                    existingApp.Motivation = request.ApplicationMotivation!;
                    existingApp.AdditionalInfo = request.ApplicationAdditionalInfo;
                    existingApp.MembershipTier = selectedTier;
                    existingApp.SignificantContribution = selectedTier == MembershipTier.Asociado
                        ? request.ApplicationSignificantContribution : null;
                    existingApp.RoleUnderstanding = selectedTier == MembershipTier.Asociado
                        ? request.ApplicationRoleUnderstanding : null;
                    existingApp.UpdatedAt = now;
                }
                else
                {
                    var application = new MemberApplication
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        MembershipTier = selectedTier,
                        Motivation = request.ApplicationMotivation!,
                        AdditionalInfo = request.ApplicationAdditionalInfo,
                        SignificantContribution = selectedTier == MembershipTier.Asociado
                            ? request.ApplicationSignificantContribution : null,
                        RoleUnderstanding = selectedTier == MembershipTier.Asociado
                            ? request.ApplicationRoleUnderstanding : null,
                        Language = language,
                        SubmittedAt = now,
                        UpdatedAt = now
                    };
                    _dbContext.Applications.Add(application);
                }
            }
        }

        // Update display name on user
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is not null)
        {
            user.DisplayName = displayName;
        }

        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateNotificationMeters();
        _cache.InvalidateActiveTeams();

        // Update profile cache if profile is approved
        if (profile.IsApproved && !profile.IsSuspended && user is not null)
        {
            await _dbContext.Entry(profile).Collection(p => p.VolunteerHistory).LoadAsync(ct);
            UpdateProfileCache(userId, CachedProfile.Create(profile, user));
        }

        // Check consent eligibility
        await _onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        _logger.LogInformation("User {UserId} updated their profile", userId);

        return profile.Id;
    }

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (user.IsDeletionPending)
            return new OnboardingResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();
        var deletionDate = now.Plus(Duration.FromDays(30));

        user.DeletionRequestedAt = now;
        user.DeletionScheduledFor = deletionDate;

        // Revoke team memberships and role assignments immediately
        await _dbContext.Entry(user).Collection(u => u.TeamMemberships).LoadAsync(ct);
        await _dbContext.Entry(user).Collection(u => u.RoleAssignments).LoadAsync(ct);

        var endedMemberships = 0;
        var activeMemberIds = user.TeamMemberships.Where(m => m.LeftAt is null).Select(m => m.Id).ToList();

        // Remove role assignments for departing memberships
        if (activeMemberIds.Count > 0)
        {
            var roleAssignments = await _dbContext.Set<TeamRoleAssignment>()
                .Where(a => activeMemberIds.Contains(a.TeamMemberId))
                .ToListAsync(ct);
            _dbContext.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);
        }

        foreach (var membership in user.TeamMemberships.Where(m => m.LeftAt is null))
        {
            membership.LeftAt = now;
            endedMemberships++;
        }

        var endedRoles = 0;
        foreach (var role in user.RoleAssignments.Where(r => r.ValidTo is null))
        {
            role.ValidTo = now;
            endedRoles++;
        }

        await _auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), user.Id,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            user.Id);

        await _dbContext.SaveChangesAsync(ct);
        UpdateProfileCache(userId, null);
        _cache.InvalidateUserAccess(userId);

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}. " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            user.Id, deletionDate, endedMemberships, endedRoles);

        // Send confirmation email
        await _dbContext.Entry(user).Collection(u => u.UserEmails).LoadAsync(ct);
        var effectiveEmail = user.GetEffectiveEmail();
        if (effectiveEmail is not null)
        {
            await _emailService.SendAccountDeletionRequestedAsync(
                effectiveEmail,
                user.DisplayName,
                deletionDate.ToDateTimeUtc(),
                user.PreferredLanguage,
                ct);
        }

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        await _dbContext.SaveChangesAsync(ct);

        // Re-add to profile cache if approved
        var profile = await _dbContext.Profiles
            .Include(p => p.VolunteerHistory)
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is { IsApproved: true, IsSuspended: false })
        {
            UpdateProfileCache(userId, CachedProfile.Create(profile, user));
        }

        _logger.LogInformation("User {UserId} cancelled account deletion request", userId);

        return new OnboardingResult(true);
    }

    public async Task<object> ExportDataAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        var applications = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);

        var consents = await _dbContext.ConsentRecords
            .AsNoTracking()
            .Include(c => c.DocumentVersion)
                .ThenInclude(v => v.LegalDocument)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.ConsentedAt)
            .ToListAsync(ct);

        var teamMemberships = await _dbContext.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.Team)
            .Where(tm => tm.UserId == userId)
            .OrderByDescending(tm => tm.JoinedAt)
            .ToListAsync(ct);

        var contactFields = profile is not null
            ? await _dbContext.ContactFields
                .AsNoTracking()
                .Where(cf => cf.ProfileId == profile.Id)
                .OrderBy(cf => cf.DisplayOrder)
                .ToListAsync(ct)
            : [];

        var roleAssignments = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId)
            .ToListAsync(ct);

        var userEmails = await _dbContext.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.DisplayOrder)
            .ToListAsync(ct);

        _logger.LogInformation("User {UserId} exported their data", userId);

        return new
        {
            ExportedAt = _clock.GetCurrentInstant().ToInvariantInstantString(),
            Account = user is not null ? new
            {
                user.Id,
                user.Email,
                user.DisplayName,
                CreatedAt = user.CreatedAt.ToInvariantInstantString(),
                LastLoginAt = user.LastLoginAt.ToInvariantInstantString()
            } : null,
            UserEmails = userEmails.Select(e => new
            {
                e.Email,
                e.IsVerified,
                e.IsOAuth,
                e.IsNotificationTarget,
                e.Visibility
            }),
            Profile = profile is not null ? new
            {
                profile.BurnerName,
                profile.FirstName,
                profile.LastName,
                Birthday = profile.DateOfBirth is not null ? $"{profile.DateOfBirth.Value.Month:D2}-{profile.DateOfBirth.Value.Day:D2}" : null,
                profile.City,
                profile.CountryCode,
                profile.Bio,
                profile.Pronouns,
                profile.ContributionInterests,
                profile.BoardNotes,
                profile.IsSuspended,
                profile.EmergencyContactName,
                profile.EmergencyContactPhone,
                profile.EmergencyContactRelationship,
                profile.HasCustomProfilePicture,
                CreatedAt = profile.CreatedAt.ToInvariantInstantString(),
                UpdatedAt = profile.UpdatedAt.ToInvariantInstantString()
            } : null,
            ContactFields = contactFields.Select(cf => new
            {
                cf.FieldType,
                Label = cf.DisplayLabel,
                cf.Value,
                cf.Visibility
            }),
            Applications = applications.Select(a => new
            {
                a.Id,
                a.Status,
                a.MembershipTier,
                a.Motivation,
                a.AdditionalInfo,
                a.SignificantContribution,
                a.RoleUnderstanding,
                SubmittedAt = a.SubmittedAt.ToInvariantInstantString(),
                ResolvedAt = a.ResolvedAt.ToInvariantInstantString()
            }),
            Consents = consents.Select(c => new
            {
                DocumentName = c.DocumentVersion.LegalDocument.Name,
                DocumentVersion = c.DocumentVersion.VersionNumber,
                c.ExplicitConsent,
                ConsentedAt = c.ConsentedAt.ToInvariantInstantString(),
                c.IpAddress,
                c.UserAgent
            }),
            TeamMemberships = teamMemberships.Select(tm => new
            {
                TeamName = tm.Team.Name,
                tm.Role,
                JoinedAt = tm.JoinedAt.ToInvariantInstantString(),
                LeftAt = tm.LeftAt.ToInvariantInstantString()
            }),
            RoleAssignments = roleAssignments.Select(ra => new
            {
                ra.RoleName,
                ValidFrom = ra.ValidFrom.ToInvariantInstantString(),
                ValidTo = ra.ValidTo.ToInvariantInstantString(),
                ra.CreatedByUserId
            })
        };
    }

    public async Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default)
    {
        var colaboradorCount = await _dbContext.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Colaborador && !p.IsSuspended, ct);
        var asociadoCount = await _dbContext.Profiles
            .CountAsync(p => p.MembershipTier == MembershipTier.Asociado && !p.IsSuspended, ct);

        return (colaboradorCount, asociadoCount);
    }

    public async Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
            return [];

        return await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => userIdList.Contains(p.UserId) && p.ProfilePictureData != null)
            .Select(p => new { p.Id, p.UserId, p.UpdatedAt })
            .AsAsyncEnumerable()
            .Select(p => (p.Id, p.UserId, p.UpdatedAt.ToUnixTimeTicks()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string? ProfilePictureUrl, bool HasCustomPicture, Guid ProfileId, int Day, int Month)>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default)
    {
        var cached = await GetCachedProfilesAsync(ct);
        return cached.Values
            .Where(p => p.BirthdayMonth == month && p.BirthdayDay.HasValue)
            .OrderBy(p => p.BirthdayDay)
            .Select(p => (p.UserId, p.DisplayName, p.ProfilePictureUrl, p.HasCustomPicture, p.ProfileId, p.BirthdayDay!.Value, p.BirthdayMonth!.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string? ProfilePictureUrl, double Latitude, double Longitude, string? City, string? CountryCode)>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default)
    {
        var cached = await GetCachedProfilesAsync(ct);
        return cached.Values
            .Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
            .Select(p => (p.UserId, p.DisplayName, p.ProfilePictureUrl, p.Latitude!.Value, p.Longitude!.Value, p.City, p.CountryCode))
            .ToList();
    }

    public async Task<IReadOnlyList<Application.DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default)
    {
        var users = await _dbContext.Users
            .Select(u => new
            {
                u.Id,
                Email = u.Email ?? string.Empty,
                u.DisplayName,
                u.ProfilePictureUrl,
                CreatedAt = u.CreatedAt.ToDateTimeUtc(),
                LastLoginAt = u.LastLoginAt != null ? u.LastLoginAt.Value.ToDateTimeUtc() : (DateTime?)null,
                HasProfile = u.Profile != null,
                IsApproved = u.Profile != null && u.Profile.IsApproved,
            })
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            users = users
                .Where(u =>
                    u.Email.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    u.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Partition once upfront — used for both filtering and status label assignment
        var allIds = users.Select(u => u.Id).ToList();
        var partition = await _membershipCalculator.PartitionUsersAsync(allIds, ct);

        // Apply filter using partition buckets
        HashSet<Guid>? filteredIds = statusFilter?.ToLowerInvariant() switch
        {
            "active" => partition.Active,
            "missingconsents" => partition.MissingConsents,
            "pending" => partition.PendingApproval,
            "suspended" => partition.Suspended,
            "incomplete" => partition.IncompleteSignup,
            "deleting" => partition.PendingDeletion,
            _ => null
        };

        var rows = filteredIds is not null
            ? users.Where(u => filteredIds.Contains(u.Id)).ToList()
            : users;

        return rows.Select(r => new Application.DTOs.AdminHumanRow(
            r.Id,
            r.Email,
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

    public async Task<Application.DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .Include(u => u.Applications)
            .Include(u => u.ConsentRecords)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        var roleAssignments = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Include(ra => ra.CreatedByUser)
            .Where(ra => ra.UserId == userId)
            .OrderByDescending(ra => ra.ValidFrom)
            .ToListAsync(ct);

        string? rejectedByName = null;
        if (user.Profile?.RejectedByUserId is not null)
        {
            var rejectedByUser = await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == user.Profile.RejectedByUserId.Value, ct);
            rejectedByName = rejectedByUser?.DisplayName;
        }

        return new Application.DTOs.AdminHumanDetailData(
            user,
            user.Profile,
            user.Applications.OrderByDescending(a => a.SubmittedAt).ToList(),
            user.ConsentRecords.Count,
            roleAssignments,
            rejectedByName);
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var pendingRecord = await _dbContext.UserEmails
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == pendingEmailId, ct);

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
        var pattern = $"%{query}%";
        return await _dbContext.Users
            .Where(u => u.Profile != null && u.Profile.IsApproved && !u.Profile.IsSuspended)
            .Where(u => EF.Functions.ILike(u.DisplayName, pattern) ||
                         (u.Email != null && EF.Functions.ILike(u.Email, pattern)))
            .OrderBy(u => u.DisplayName)
            .Take(20)
            .Select(u => new UserSearchResult(u.Id, u.DisplayName, u.Email ?? ""))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(string query, CancellationToken ct = default)
    {
        var cached = await GetCachedProfilesAsync(ct);
        var results = new List<HumanSearchResult>();

        foreach (var p in cached.Values)
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

    // ==========================================================================
    // Profile Cache
    // ==========================================================================

    private async Task<ConcurrentDictionary<Guid, CachedProfile>> GetCachedProfilesAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrCreateAsync(CacheKeys.ApprovedProfiles, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var profiles = await _dbContext.Profiles
                .AsNoTracking()
                .Include(p => p.User)
                .Include(p => p.VolunteerHistory)
                .Where(p => p.IsApproved && !p.IsSuspended)
                .ToListAsync(ct);

            return new ConcurrentDictionary<Guid, CachedProfile>(
                profiles.ToDictionary(
                    p => p.UserId,
                    p => CachedProfile.Create(p, p.User)));
        }) ?? new();
    }

    public void UpdateProfileCache(Guid userId, CachedProfile? newValue)
        => _cache.UpdateApprovedProfile(userId, newValue);

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

    // ==========================================================================
    // Volunteer Event Profiles
    // ==========================================================================

    public async Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId)
    {
        var existing = await _dbContext.VolunteerEventProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (existing is not null)
            return existing;

        var now = _clock.GetCurrentInstant();
        var profile = new VolunteerEventProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.VolunteerEventProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        return profile;
    }

    public async Task UpdateShiftProfileAsync(VolunteerEventProfile profile)
    {
        profile.UpdatedAt = _clock.GetCurrentInstant();
        _dbContext.VolunteerEventProfiles.Update(profile);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<VolunteerEventProfile?> GetShiftProfileAsync(Guid userId, bool includeMedical)
    {
        var profile = await _dbContext.VolunteerEventProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile is not null && !includeMedical)
        {
            profile.MedicalConditions = null;
        }

        return profile;
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
