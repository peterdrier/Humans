using Humans.Consent.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Tickets.Services;

/// <summary>Admin-card status of the shared gate-terminal account.</summary>
internal sealed record GateTerminalStatus(bool Provisioned, bool HasPassword, Instant? LastLoginAt);

/// <summary>
/// Provisions and manages the shared gate-terminal account
/// (<see cref="SystemUserIds.GateTerminal"/>): a real User row, not a person.
/// Created lazily the first time a ticket admin sets its password from
/// /Tickets/Admin/Gate. Mirrors Development's <c>DevPersonaSeeder</c>: User via
/// <see cref="UserManager{TUser}"/>, Profile via the canonical
/// <see cref="IProfileEditorService.SaveProfileAsync"/> path (so
/// <c>UserStateClassifier</c> lifts the state to Active), consent check cleared
/// so the account never sits in the Consent Coordinator review queue.
/// The password lives on the Identity row itself — no extra credential storage.
/// </summary>
internal sealed class GateTerminalAccountSeeder(
    UserManager<User> userManager,
    IServiceScopeFactory scopeFactory,
    IProfileEditorService profileEditorService,
    IConsentWrite consentWrite,
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
    /// source IP instead (Shell's <c>GateLoginThrottle</c>).
    /// </summary>
    public async Task<IdentityResult> SetPasswordAsync(string password, Guid actorUserId)
    {
        var user = await userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString());
        // No password means initial setup never completed. Resume it even when
        // creating the User succeeded before a later provisioning step failed.
        if (user is null || !await userManager.HasPasswordAsync(user))
            user = await ProvisionAsync(user, actorUserId);
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "Gate account provisioning failed." });

        // Profile/onboarding writes use their own contexts. A fresh Identity scope
        // prevents its tracked pre-setup User from overwriting the saved names/state.
        await using var credentialScope = scopeFactory.CreateAsyncScope();
        var credentialManager = credentialScope.ServiceProvider.GetRequiredService<UserManager<User>>();
        user = await credentialManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString());
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "Gate account not found." });

        // Validate-then-replace: ResetPasswordAsync checks the new password against
        // the policy BEFORE touching the stored hash, so a rejected rotation leaves
        // the old password (and live gate sessions) intact. On success it bumps the
        // security stamp, signing out existing gate sessions.
        var token = await credentialManager.GeneratePasswordResetTokenAsync(user);
        var reset = await credentialManager.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded)
            return reset;

        await credentialManager.SetLockoutEnabledAsync(user, false);

        await auditLogService.LogAsync(
            AuditAction.GateTerminalPasswordSet,
            nameof(User),
            SystemUserIds.GateTerminal,
            "Gate terminal password set",
            actorUserId);

        logger.LogWarning("Gate terminal password set by {ActorUserId}", actorUserId);
        return reset;
    }

    private async Task<User?> ProvisionAsync(User? user, Guid actorUserId)
    {
        if (user is null)
        {
            user = new User
            {
                Id = SystemUserIds.GateTerminal,
                DisplayName = SystemUserIds.GateTerminalDisplayName,
                BurnerName = SystemUserIds.GateTerminalDisplayName,
                CreatedAt = clock.GetCurrentInstant()
            };

            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                logger.LogError("Failed to create gate terminal account: {Errors}",
                    string.Join(", ", created.Errors.Select(e => e.Description)));
                return null;
            }
        }

        // Names via the canonical profile path so UserStateClassifier yields Active
        // (the membership filter requires it); no email, no teams, no roles.
        var profileSave = await profileEditorService.SaveProfileAsync(
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

        if (!profileSave.Succeeded)
        {
            logger.LogWarning("Gate terminal profile save refused: {ErrorKey}", profileSave.ErrorKey);
            return null;
        }

        // Keep the account out of the Consent Coordinator review queue.
        var consentResult = await consentWrite.RecordConsentCheckAsync(
            SystemUserIds.GateTerminal,
            ConsentCheckStatus.Cleared,
            actorUserId,
            "System gate-terminal account — not a person");
        if (!consentResult.Success)
        {
            logger.LogWarning("Gate terminal consent-check clear failed: {ErrorKey}", consentResult.ErrorKey);
            return null;
        }

        cache.InvalidateUserAccess(SystemUserIds.GateTerminal);
        await userInfoInvalidator.InvalidateAsync(SystemUserIds.GateTerminal);

        logger.LogWarning("Gate terminal account provisioned by {ActorUserId}", actorUserId);
        return user;
    }
}
