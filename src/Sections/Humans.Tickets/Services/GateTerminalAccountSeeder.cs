using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Humans.Tickets.Services;

internal sealed record GateTerminalStatus(bool Provisioned, bool HasPassword, Instant? LastLoginAt);

/// <summary>
/// A real User row, not a person, created on the first password set and marked fake in Users, which keeps it out of
/// the Consent Coordinator review queue.
/// </summary>
internal sealed class GateTerminalAccountSeeder(
    IUserSignInService signIn,
    IUserService userService,
    IAccountProvisioningService accountProvisioning,
    IProfileEditorService profileEditorService,
    IUserWrite userWrite,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<GateTerminalAccountSeeder> logger)
{
    public async Task<GateTerminalStatus> GetStatusAsync()
    {
        var user = await userService.GetUserInfoAsync(SystemUserIds.GateTerminal);
        if (user is null)
            return new GateTerminalStatus(false, false, null);

        var hasPassword = await signIn.HasPasswordAsync(SystemUserIds.GateTerminal);
        return new GateTerminalStatus(true, hasPassword, user.LastLoginAt);
    }

    /// <summary>
    /// Rotation bumps the security stamp, ending existing gate sessions. Lockout is off: the username is public, so
    /// lockout would let anyone lock out the real terminal. Shell's <c>GateLoginThrottle</c> throttles per IP instead.
    /// </summary>
    public async Task<IdentityResult> SetPasswordAsync(string password, Guid actorUserId)
    {
        // No password means initial setup never completed. Resume it even when
        // creating the User succeeded before a later provisioning step failed.
        if (!await signIn.HasPasswordAsync(SystemUserIds.GateTerminal)
            && !await ProvisionAsync(actorUserId))
            return IdentityResult.Failed(new IdentityError { Description = "Gate account provisioning failed." });

        // Every set, not just provisioning: an account provisioned before the marker existed gets it on rotation.
        await userWrite.MarkFakeAsync(SystemUserIds.GateTerminal, actorUserId, "System gate-terminal account, not a person");

        var reset = await signIn.SetPasswordAsync(SystemUserIds.GateTerminal, password);
        if (!reset.Succeeded)
            return reset;

        await auditLogService.LogAsync(
            TicketsAuditActions.GateTerminalPasswordSet,
            AuditSubjectTypes.User,
            SystemUserIds.GateTerminal,
            "Gate terminal password set",
            actorUserId);

        logger.LogWarning("Gate terminal password set by {ActorUserId}", actorUserId);
        return reset;
    }

    private async Task<bool> ProvisionAsync(Guid actorUserId)
    {
        if (await userService.GetUserInfoAsync(SystemUserIds.GateTerminal) is null)
        {
            var errors = await accountProvisioning.CreateAccountAsync(
                SystemUserIds.GateTerminal, SystemUserIds.GateTerminalDisplayName, clock.GetCurrentInstant());
            if (errors.Count > 0)
            {
                logger.LogError("Failed to create gate terminal account: {Errors}", string.Join(", ", errors));
                return false;
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
            return false;
        }

        logger.LogWarning("Gate terminal account provisioned by {ActorUserId}", actorUserId);
        return true;
    }
}
