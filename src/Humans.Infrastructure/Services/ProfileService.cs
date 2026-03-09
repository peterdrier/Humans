using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces;
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

    public async Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _dbContext.Profiles
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        var isTierLocked = profile != null && await _dbContext.Applications
            .AnyAsync(a => a.UserId == userId &&
                (a.Status == ApplicationStatus.Submitted ||
                 a.Status == ApplicationStatus.Approved), ct);

        MemberApplication? pendingApplication = null;
        var isInitialSetup = profile == null || !profile.IsApproved;
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

        if (profile == null)
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
        else if (request.ProfilePictureData != null && request.ProfilePictureContentType != null)
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

                if (existingApp != null)
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
        if (user != null)
        {
            user.DisplayName = displayName;
        }

        await _dbContext.SaveChangesAsync(ct);
        _cache.Remove(CacheKeys.NavBadgeCounts);

        // Check consent eligibility
        await _onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        _logger.LogInformation("User {UserId} updated their profile", userId);

        return profile.Id;
    }

    public async Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user == null)
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
        foreach (var membership in user.TeamMemberships.Where(m => m.LeftAt == null))
        {
            membership.LeftAt = now;
            endedMemberships++;
        }

        var endedRoles = 0;
        foreach (var role in user.RoleAssignments.Where(r => r.ValidTo == null))
        {
            role.ValidTo = now;
            endedRoles++;
        }

        await _auditLogService.LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, "User", user.Id,
            $"Revoked {endedMemberships} team membership(s) and {endedRoles} role assignment(s) on deletion request",
            user.Id, user.DisplayName);

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogWarning(
            "User {UserId} requested account deletion. Scheduled for {DeletionDate}. " +
            "Revoked {MembershipCount} memberships and {RoleCount} roles immediately",
            user.Id, deletionDate, endedMemberships, endedRoles);

        // Send confirmation email
        await _dbContext.Entry(user).Collection(u => u.UserEmails).LoadAsync(ct);
        var effectiveEmail = user.GetEffectiveEmail();
        if (effectiveEmail != null)
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
        if (user == null)
            return new OnboardingResult(false, "NotFound");

        if (!user.IsDeletionPending)
            return new OnboardingResult(false, "NoDeletionPending");

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        await _dbContext.SaveChangesAsync(ct);

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

        var contactFields = profile != null
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
            ExportedAt = _clock.GetCurrentInstant().ToString(null, CultureInfo.InvariantCulture),
            Account = user != null ? new
            {
                user.Id,
                user.Email,
                user.DisplayName,
                CreatedAt = user.CreatedAt.ToString(null, CultureInfo.InvariantCulture),
                LastLoginAt = user.LastLoginAt?.ToString(null, CultureInfo.InvariantCulture)
            } : null,
            UserEmails = userEmails.Select(e => new
            {
                e.Email,
                e.IsVerified,
                e.IsOAuth,
                e.IsNotificationTarget,
                e.Visibility
            }),
            Profile = profile != null ? new
            {
                profile.BurnerName,
                profile.FirstName,
                profile.LastName,
                Birthday = profile.DateOfBirth != null ? $"{profile.DateOfBirth.Value.Month:D2}-{profile.DateOfBirth.Value.Day:D2}" : null,
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
                CreatedAt = profile.CreatedAt.ToString(null, CultureInfo.InvariantCulture),
                UpdatedAt = profile.UpdatedAt.ToString(null, CultureInfo.InvariantCulture)
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
                SubmittedAt = a.SubmittedAt.ToString(null, CultureInfo.InvariantCulture),
                ResolvedAt = a.ResolvedAt?.ToString(null, CultureInfo.InvariantCulture)
            }),
            Consents = consents.Select(c => new
            {
                DocumentName = c.DocumentVersion.LegalDocument.Name,
                DocumentVersion = c.DocumentVersion.VersionNumber,
                c.ExplicitConsent,
                ConsentedAt = c.ConsentedAt.ToString(null, CultureInfo.InvariantCulture),
                c.IpAddress,
                c.UserAgent
            }),
            TeamMemberships = teamMemberships.Select(tm => new
            {
                TeamName = tm.Team.Name,
                tm.Role,
                JoinedAt = tm.JoinedAt.ToString(null, CultureInfo.InvariantCulture),
                LeftAt = tm.LeftAt?.ToString(null, CultureInfo.InvariantCulture)
            }),
            RoleAssignments = roleAssignments.Select(ra => new
            {
                ra.RoleName,
                ValidFrom = ra.ValidFrom.ToString(null, CultureInfo.InvariantCulture),
                ValidTo = ra.ValidTo?.ToString(null, CultureInfo.InvariantCulture),
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
        return await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => userIdList.Contains(p.UserId) && p.ProfilePictureData != null)
            .Select(p => new ValueTuple<Guid, Guid, long>(p.Id, p.UserId, p.UpdatedAt.ToUnixTimeTicks()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string? ProfilePictureUrl, bool HasCustomPicture, Guid ProfileId, int Day, int Month)>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default)
    {
        return await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.DateOfBirth != null && !p.IsSuspended)
            .Where(p => p.DateOfBirth!.Value.Month == month)
            .OrderBy(p => p.DateOfBirth!.Value.Day)
            .Select(p => new ValueTuple<Guid, string, string?, bool, Guid, int, int>(
                p.UserId,
                p.User.DisplayName,
                p.User.ProfilePictureUrl,
                p.ProfilePictureData != null,
                p.Id,
                p.DateOfBirth!.Value.Day,
                p.DateOfBirth!.Value.Month))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string? ProfilePictureUrl, double Latitude, double Longitude, string? City, string? CountryCode)>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default)
    {
        return await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.Latitude != null && p.Longitude != null && !p.IsSuspended && p.IsApproved)
            .Select(p => new ValueTuple<Guid, string, string?, double, double, string?, string?>(
                p.UserId,
                p.User.DisplayName,
                p.User.ProfilePictureUrl,
                p.Latitude!.Value,
                p.Longitude!.Value,
                p.City,
                p.CountryCode))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Application.DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default)
    {
        var query = _dbContext.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u =>
                u.Email!.Contains(search) ||
                u.DisplayName.Contains(search));
        }

        switch (statusFilter?.ToLowerInvariant())
        {
            case "active":
                query = query.Where(u => u.Profile != null && u.Profile.IsApproved && !u.Profile.IsSuspended);
                break;
            case "pending":
                query = query.Where(u => u.Profile != null && !u.Profile.IsApproved && !u.Profile.IsSuspended);
                break;
            case "suspended":
                query = query.Where(u => u.Profile != null && u.Profile.IsSuspended);
                break;
            case "inactive":
                query = query.Where(u => u.Profile == null);
                break;
            case "deleting":
                query = query.Where(u => u.DeletionRequestedAt != null);
                break;
        }

        return await query
            .Select(u => new Application.DTOs.AdminHumanRow(
                u.Id,
                u.Email ?? string.Empty,
                u.DisplayName,
                u.ProfilePictureUrl,
                u.CreatedAt.ToDateTimeUtc(),
                u.LastLoginAt != null ? u.LastLoginAt.Value.ToDateTimeUtc() : null,
                u.Profile != null,
                u.Profile != null && u.Profile.IsApproved,
                u.Profile != null
                    ? (u.Profile.IsSuspended ? "Suspended" : (!u.Profile.IsApproved ? "Pending Approval" : "Active"))
                    : "Inactive"))
            .ToListAsync(ct);
    }

    public async Task<Application.DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users
            .Include(u => u.Profile)
            .Include(u => u.Applications)
            .Include(u => u.ConsentRecords)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user == null)
            return null;

        var roleAssignments = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Include(ra => ra.CreatedByUser)
            .Where(ra => ra.UserId == userId)
            .OrderByDescending(ra => ra.ValidFrom)
            .ToListAsync(ct);

        var auditEntries = await _dbContext.AuditLogEntries
            .AsNoTracking()
            .Where(e =>
                (e.EntityType == "User" && e.EntityId == userId) ||
                (e.RelatedEntityId == userId))
            .OrderByDescending(e => e.OccurredAt)
            .Take(50)
            .Select(e => new Application.DTOs.AdminAuditEntry(
                e.Action.ToString(),
                e.Description,
                e.OccurredAt.ToDateTimeUtc(),
                e.ActorName,
                e.ActorUserId == null))
            .ToListAsync(ct);

        string? rejectedByName = null;
        if (user.Profile?.RejectedByUserId != null)
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
            auditEntries,
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
}
