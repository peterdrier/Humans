using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Web.Infrastructure;

/// <summary>Admin-card status of the shared gate-terminal account.</summary>
public record GateTerminalStatus(bool Provisioned, bool HasPassword, Instant? LastLoginAt);

/// <summary>
/// Provisions and manages the shared gate-terminal account
/// (<see cref="SystemUserIds.GateTerminal"/>): a real User row, not a person.
/// Created lazily the first time a ticket admin sets its password from
/// /Tickets/Admin/Gate. Mirrors <see cref="DevPersonaSeeder"/>: User via
/// <see cref="UserManager{TUser}"/>, Profile via the canonical
/// <see cref="IProfileEditorService.SaveProfileAsync"/> path (so
/// <c>UserStateClassifier</c> lifts the state to Active), consent check cleared
/// so the account never sits in the Consent Coordinator review queue.
/// The password lives on the Identity row itself — no extra credential storage.
/// </summary>
public sealed class GateTerminalAccountSeeder(
    UserManager<User> userManager,
    IProfileEditorService profileEditorService,
    IUserService userService,
    IUserInfoInvalidator userInfoInvalidator,
    IAuditLogService auditLogService,
    IClock clock,
    IMemoryCache cache,
    ILogger<GateTerminalAccountSeeder> logger)
{
    public async Task<GateTerminalStatus> GetStatusAsync()
    {
        var user = await userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString());
        if (user is null)
            return new GateTerminalStatus(false, false, null);

        var hasPassword = await userManager.HasPasswordAsync(user);
        return new GateTerminalStatus(true, hasPassword, user.LastLoginAt);
    }

    /// <summary>
    /// Sets (or rotates) the gate password, provisioning the account on first use.
    /// Rotation bumps the Identity security stamp, so existing gate sessions die
    /// at the next security-stamp validation sweep. Identity lockout is disabled
    /// on the account: the username is public, so per-account lockout would let
    /// anyone deny the real terminal at gate. Sign-in failures are throttled per
    /// source IP instead (<see cref="GateLoginThrottle"/>).
    /// </summary>
    public async Task<IdentityResult> SetPasswordAsync(string password, Guid actorUserId)
    {
        var user = await userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString())
            ?? await ProvisionAsync(actorUserId);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "Gate account provisioning failed." });

        // Validate-then-replace: ResetPasswordAsync checks the new password against
        // the policy BEFORE touching the stored hash, so a rejected rotation leaves
        // the old password (and live gate sessions) intact. On success it bumps the
        // security stamp, signing out existing gate sessions.
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded)
            return reset;

        await userManager.SetLockoutEnabledAsync(user, false);

        await auditLogService.LogAsync(
            AuditAction.GateTerminalPasswordSet,
            nameof(User),
            SystemUserIds.GateTerminal,
            "Gate terminal password set",
            actorUserId);

        logger.LogWarning("Gate terminal password set by {ActorUserId}", actorUserId);
        return reset;
    }

    private async Task<User?> ProvisionAsync(Guid actorUserId)
    {
        var now = clock.GetCurrentInstant();
#pragma warning disable HUM_USER_DISPLAYNAME // System-account seeding writes the legacy Identity fallback column (same as DevPersonaSeeder).
        var user = new User
        {
            Id = SystemUserIds.GateTerminal,
            DisplayName = SystemUserIds.GateTerminalDisplayName,
            CreatedAt = now
        };
#pragma warning restore HUM_USER_DISPLAYNAME

        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
        {
            logger.LogError("Failed to create gate terminal account: {Errors}",
                string.Join(", ", created.Errors.Select(e => e.Description)));
            return null;
        }

        // Names via the canonical profile path so UserStateClassifier yields Active
        // (the membership filter requires it); no email, no teams, no roles.
        await profileEditorService.SaveProfileAsync(
            SystemUserIds.GateTerminal,
            SystemUserIds.GateTerminalDisplayName,
            new ProfileSaveRequest(
                BurnerName: SystemUserIds.GateTerminalDisplayName,
                FirstName: "Gate",
                LastName: "Terminal",
                City: null, CountryCode: null, Latitude: null, Longitude: null, PlaceId: null,
                Bio: "Shared gate-terminal account for the ticket lookup tool. Not a person.",
                Pronouns: null, ContributionInterests: null, BoardNotes: null,
                BirthdayMonth: null, BirthdayDay: null,
                EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelationship: null,
                NoPriorBurnExperience: false,
                ProfilePictureData: null, ProfilePictureContentType: null, RemoveProfilePicture: false));

        // Keep the account out of the Consent Coordinator review queue.
        var consentResult = await userService.ApplyProfileOnboardingMutationAsync(
            SystemUserIds.GateTerminal,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RecordConsentCheck,
                ActorUserId: actorUserId,
                ConsentCheckStatus: ConsentCheckStatus.Cleared,
                Notes: "System gate-terminal account — not a person"));
        if (!consentResult.Success)
        {
            logger.LogWarning("Gate terminal consent-check clear failed: {ErrorKey}", consentResult.ErrorKey);
        }

        cache.InvalidateUserAccess(SystemUserIds.GateTerminal);
        await userInfoInvalidator.InvalidateAsync(SystemUserIds.GateTerminal);

        logger.LogWarning("Gate terminal account provisioned by {ActorUserId}", actorUserId);
        return user;
    }
}
