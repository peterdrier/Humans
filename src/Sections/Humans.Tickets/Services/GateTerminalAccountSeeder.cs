using Humans.Consent.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed record GateTerminalStatus(bool Provisioned, bool HasPassword, Instant? LastLoginAt);

/// <summary>
/// A real User row, not a person, created on the first password set. Its consent check is cleared so it never sits
/// in the Consent Coordinator review queue.
/// </summary>
internal sealed class GateTerminalAccountSeeder(
    UserManager<User> userManager,
    IServiceScopeFactory scopeFactory,
    IProfileEditorService profileEditorService,
    IConsentWrite consentWrite,
    IAuditLogService auditLogService,
    IClock clock,
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
    /// Rotation bumps the security stamp, ending existing gate sessions. Lockout is off: the username is public, so
    /// lockout would let anyone lock out the real terminal. Shell's <c>GateLoginThrottle</c> throttles per IP instead.
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

        // ResetPasswordAsync validates before replacing the hash, so a rejected rotation keeps the old password and
        // live gate sessions.
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


        logger.LogWarning("Gate terminal account provisioned by {ActorUserId}", actorUserId);
        return user;
    }
}
