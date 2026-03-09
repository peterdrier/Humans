using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces;

public record ProfileSaveRequest(
    string BurnerName, string FirstName, string LastName,
    string? City, string? CountryCode, double? Latitude, double? Longitude, string? PlaceId,
    string? Bio, string? Pronouns, string? ContributionInterests, string? BoardNotes,
    int? BirthdayMonth, int? BirthdayDay,
    string? EmergencyContactName, string? EmergencyContactPhone, string? EmergencyContactRelationship,
    bool NoPriorBurnExperience,
    byte[]? ProfilePictureData, string? ProfilePictureContentType, bool RemoveProfilePicture,
    MembershipTier? SelectedTier, string? ApplicationMotivation, string? ApplicationAdditionalInfo,
    string? ApplicationSignificantContribution, string? ApplicationRoleUnderstanding);

public interface IProfileService
{
    Task<Profile?> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<(Profile? Profile, MemberApplication? LatestApplication, int PendingConsentCount)>
        GetProfileIndexDataAsync(Guid userId, CancellationToken ct = default);
    Task<(Profile? Profile, bool IsTierLocked, MemberApplication? PendingApplication)>
        GetProfileEditDataAsync(Guid userId, CancellationToken ct = default);
    Task<(byte[]? Data, string? ContentType)> GetProfilePictureAsync(Guid profileId, CancellationToken ct = default);
    Task<Guid> SaveProfileAsync(Guid userId, string displayName, ProfileSaveRequest request, string language, CancellationToken ct = default);
    Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default);
    Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default);
    Task<object> ExportDataAsync(Guid userId, CancellationToken ct = default);
    Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default);

    Task<(int ColaboradorCount, int AsociadoCount)> GetTierCountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>
        GetCustomPictureInfoByUserIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid UserId, string DisplayName, string? ProfilePictureUrl, bool HasCustomPicture, Guid ProfileId, int Day, int Month)>>
        GetBirthdayProfilesAsync(int month, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid UserId, string DisplayName, string? ProfilePictureUrl, double Latitude, double Longitude, string? City, string? CountryCode)>>
        GetApprovedProfilesWithLocationAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DTOs.AdminHumanRow>> GetFilteredHumansAsync(
        string? search, string? statusFilter, CancellationToken ct = default);

    Task<DTOs.AdminHumanDetailData?> GetAdminHumanDetailAsync(Guid userId, CancellationToken ct = default);
}
