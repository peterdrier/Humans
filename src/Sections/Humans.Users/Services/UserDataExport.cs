using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Domain;

namespace Humans.Users.Services;

internal sealed class UserDataExport(IUserRepository repo) : IApplicationService
{
    internal const string Account = "Account";
    internal const string Profile = "Profile";
    internal const string MergedProfiles = "MergedProfiles";
    internal const string ContactFields = "ContactFields";
    internal const string UserEmails = "UserEmails";
    internal const string VolunteerHistory = "VolunteerHistory";
    internal const string Languages = "Languages";

    internal async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var allUsers = (await repo.GetAllAsync(ct)).ToDictionary(u => u.Id);
        if (!allUsers.TryGetValue(userId, out var user))
        {
            return [
                new UserDataSlice(Account, null),
                new UserDataSlice(Profile, null)
            ];
        }

        var visited = new HashSet<Guid> { userId };
        while (user.MergedToUserId is { } next && visited.Add(next) &&
               allUsers.TryGetValue(next, out var target))
        {
            user = target;
        }
        userId = user.Id;

        // The Workspace identity is Google's export slice; the legacy per-row flags stay here.
        var userEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        var effectiveEmail = userEmails
            .Where(e => e.IsVerified)
            .OrderByDescending(e => e.IsPrimary)
            .Select(e => e.Email)
            .FirstOrDefault() ?? user.IdentityEmailColumn;

        // The dead legacy flags stay in the Art. 15 export while the columns exist; this is their
        // only reader.
#pragma warning disable CS0618
        var unsubscribedFromCampaigns = user.UnsubscribedFromCampaigns;
        var suppressScheduleChangeEmails = user.SuppressScheduleChangeEmails;
#pragma warning restore CS0618

        var shaped = new
        {
            user.Id,
            Email = effectiveEmail,
            user.DisplayName,
            user.PreferredLanguage,
            UnsubscribedFromCampaigns = unsubscribedFromCampaigns,
            SuppressScheduleChangeEmails = suppressScheduleChangeEmails,
            ContactSource = user.ContactSource?.ToString(),
            State = user.State.ToString(),
            DeletionRequestedAt = user.DeletionRequestedAt.ToIso8601(),
            DeletionScheduledFor = user.DeletionScheduledFor.ToIso8601(),
            DeletionEligibleAfter = user.DeletionEligibleAfter.ToIso8601(),
            CreatedAt = user.CreatedAt.ToIso8601(),
            LastLoginAt = user.LastLoginAt.ToIso8601()
        };

        var profile = await repo.GetByUserIdReadOnlyAsync(userId, ct);

        var contactFields = profile is not null
            ? await repo.GetByProfileIdReadOnlyAsync(profile.Id, ct)
            : [];

        var volunteerHistory = profile?.VolunteerHistory
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToList() ?? (IReadOnlyList<VolunteerHistoryEntry>)[];

        var profileLanguages = profile is not null
            ? await repo.GetLanguagesAsync(profile.Id, ct)
            : [];

        var profileSlice = profile is null
            ? new UserDataSlice(Profile, null)
            : new UserDataSlice(Profile, ShapeProfileForExport(profile,
                user.State is UserState.Suspended or UserState.AdminSuspended));

        // Folds keep health data on archived rows, so walk back from the survivor and export every
        // retained profile.
        var sourcesByTarget = allUsers.Values.Where(u => u.MergedToUserId is not null)
            .ToLookup(u => u.MergedToUserId!.Value);
        var mergedIds = new HashSet<Guid> { userId };
        var pending = new Queue<Guid>([userId]);
        while (pending.TryDequeue(out var targetId))
        {
            foreach (var source in sourcesByTarget[targetId])
            {
                if (mergedIds.Add(source.Id))
                    pending.Enqueue(source.Id);
            }
        }
        mergedIds.Remove(userId);

        var mergedProfiles = mergedIds.Count == 0 ? [] : await repo.GetAllProfilesAsync(ct);
        var mergedProfileSlice = new UserDataSlice(MergedProfiles, mergedProfiles
            .Where(p => mergedIds.Contains(p.UserId))
            .Select(p => new
            {
                p.UserId,
                Profile = ShapeProfileForExport(p,
                    allUsers[p.UserId].State is UserState.Suspended or UserState.AdminSuspended)
            }).ToList());

        var contactFieldSlice = new UserDataSlice(ContactFields, contactFields.Select(cf => new
        {
            cf.FieldType,
            Label = cf.DisplayLabel,
            cf.Value,
            cf.Visibility
        }).ToList());

        var userEmailsSlice = new UserDataSlice(UserEmails, userEmails.Select(e => new
        {
            e.Email,
            e.IsVerified,
            // JSON keys pinned: GDPR export stability.
            IsOAuth = e.Provider != null,
            IsNotificationTarget = e.IsPrimary,
            VerificationSentAt = e.VerificationSentAt.ToIso8601(),
            CreatedAt = e.CreatedAt.ToIso8601(),
            UpdatedAt = e.UpdatedAt.ToIso8601(),
            e.Visibility
        }).ToList());

        var volunteerHistorySlice = new UserDataSlice(VolunteerHistory, volunteerHistory.Select(vh => new
        {
            Date = vh.Date.ToInvariantDate(),
            vh.EventName,
            vh.Description,
            CreatedAt = vh.CreatedAt.ToIso8601()
        }).ToList());

        var languagesSlice = new UserDataSlice(Languages, profileLanguages.Select(pl => new
        {
            pl.LanguageCode,
            pl.Proficiency
        }).ToList());

        return
        [
            new UserDataSlice(Account, shaped),
            profileSlice,
            mergedProfileSlice,
            contactFieldSlice,
            userEmailsSlice,
            volunteerHistorySlice,
            languagesSlice
        ];
    }

    private static object ShapeProfileForExport(Profile profile, bool isSuspended) =>
        new
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
            // JSON key pinned: GDPR export stability.
            IsSuspended = isSuspended,
            profile.NoPriorBurnExperience,
            profile.EmergencyContactName,
            profile.EmergencyContactPhone,
            profile.EmergencyContactRelationship,
            profile.MedicalConditions,
            profile.HasCustomProfilePicture,
            CreatedAt = profile.CreatedAt.ToIso8601(),
            UpdatedAt = profile.UpdatedAt.ToIso8601()
        };

}
