using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using Humans.Users.Contracts;

namespace Humans.Google.Services;

/// <summary>Rotates recovery credentials and preserves their delivery after a successful Workspace write.</summary>
internal sealed class WorkspaceAccountRecovery(
    IGoogleWorkspaceUserService workspaceUserService,
    IUserEmailService userEmailService,
    IAuditLogService auditLogService,
    string workspaceDomain,
    ILogger logger) : IApplicationService
{
    private async Task<Guid?> TryFindLinkedUserIdAsync(string email, CancellationToken ct)
    {
        var matches = await userEmailService.MatchByEmailsAsync([email], ct);
        return UserEmailMatchOwner.ByEmail(matches).GetValueOrDefault(email)?.UserId;
    }

    internal async Task<WorkspaceAccountActionResult> ResetPasswordAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new WorkspaceAccountActionResult(false, ErrorMessage: "Email is required.");
        }

        string newPassword;
        try
        {
            newPassword = PasswordGenerator.GenerateTemporary();
            await workspaceUserService.ResetPasswordAsync(email, newPassword, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset password for: {Email}", email);
            return new WorkspaceAccountActionResult(false,
                ErrorMessage: $"Failed to reset password for {email}.");
        }

        // Audit AFTER Workspace success. Caller MUST get the temp password even if audit fails — retry would re-rotate and lock the human out.
        try
        {
            var linkedUserId = await TryFindLinkedUserIdAsync(email, ct);
            await auditLogService.LogAsync(
                GoogleAuditActions.WorkspaceAccountPasswordReset,
                "WorkspaceAccount", linkedUserId ?? Guid.Empty,
                email,
                actorUserId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER password reset for {Email} by actor {ActorUserId}. Password was rotated; reconcile audit trail manually.",
                email, actorUserId);
        }

        return new WorkspaceAccountActionResult(true,
            Message: $"Password reset for {email}. New temporary password: {newPassword}",
            TemporaryPassword: newPassword);
    }

    private async Task<WorkspaceBackupCodesResult> GenerateBackupCodesAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> codes;
        try
        {
            codes = await workspaceUserService.GenerateBackupCodesAsync(email, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate backup codes for: {Email}", email);
            return new WorkspaceBackupCodesResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Failed to generate backup codes for {email}. Check logs for details.");
        }

        if (codes.Count == 0)
        {
            // Generated but listed none: nothing to deliver or audit, so fail clearly rather than audit zero codes.
            logger.LogWarning(
                "Backup-code generation for {Email} returned 0 codes; nothing to deliver",
                email);
            return new WorkspaceBackupCodesResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Backup codes were generated for {email} but none were returned. Check logs and try again.");
        }

        // If the audit write throws, still return the codes: Google has already invalidated the old set, so dropping
        // these locks the member out. A critical log flags it for reconciliation.
        try
        {
            // Linked accounts audit under the member's id; unlinked ones under Guid.Empty, shown by email.
            var linkedUserId = await TryFindLinkedUserIdAsync(email, ct);
            await auditLogService.LogAsync(
                GoogleAuditActions.WorkspaceAccountBackupCodesGenerated,
                "WorkspaceAccount", linkedUserId ?? Guid.Empty,
                $"{codes.Count} code(s), {email}",
                actorUserId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER backup codes were generated for {Email} by actor {ActorUserId}. Codes were delivered; reconcile audit trail manually.",
                email, actorUserId);
        }

        return new WorkspaceBackupCodesResult(
            Success: true,
            Email: email,
            Codes: codes,
            Message: $"Generated {codes.Count} backup code(s) for {email}. Deliver them securely — they cannot be retrieved again.");
    }

    internal async Task<WorkspaceRecoveryCredentialsResult> ResetPasswordAndGenerate2FaAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: "Email is required.");
        }

        // Refuse an account already enrolled in 2SV: the backup-code rotation would break its working 2FA. Checked
        // live, because hiding the button does not stop a hand-crafted POST.
        WorkspaceUserAccount? liveAccount;
        try
        {
            liveAccount = await workspaceUserService.GetAccountAsync(email, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch live 2SV state for: {Email}", email);
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Failed to verify 2FA enrollment for {email}. Aborted; no changes made.");
        }

        if (liveAccount is null)
        {
            logger.LogWarning(
                "Reset+2FA refused for {Email}: account not found in Workspace Directory. Actor: {ActorUserId}.",
                email, actorUserId);
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: $"Account {email} not found in Workspace Directory.");
        }

        if (liveAccount.IsEnrolledIn2Sv)
        {
            logger.LogWarning(
                "Reset+2FA refused for {Email}: already enrolled in 2-Step Verification. Actor: {ActorUserId}.",
                email, actorUserId);

            // If the audit write throws, log Critical and still refuse: no Workspace change happened.
            try
            {
                await auditLogService.LogAsync(
                    GoogleAuditActions.WorkspaceAccountResetBlockedFor2Sv,
                    "WorkspaceAccount", Guid.Empty,
                    $"Refused Reset+2FA for @{workspaceDomain} account {email}: already enrolled in 2-Step Verification",
                    actorUserId);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex,
                    "Audit-log write failed for Reset+2FA refusal on {Email} by actor {ActorUserId}. Refusal was still surfaced to the admin; reconcile audit trail manually.",
                    email, actorUserId);
            }

            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: $"{email} is already enrolled in 2FA. Reset+2FA is for locked-out humans only — use the plain Reset Password button, or rotate 2FA in the Google Admin console.");
        }

        // Step 1. No point grabbing a backup code if the member cannot sign in anyway.
        var resetResult = await ResetPasswordAsync(email, actorUserId, ct);
        if (!resetResult.Success || resetResult.TemporaryPassword is null)
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: false,
                Email: email,
                ErrorMessage: resetResult.ErrorMessage
                    ?? $"Failed to reset password for {email}. Check logs for details.");
        }

        // Step 2. On failure the password still works, so return it alone: Google rotates codes destructively.
        var codesResult = await GenerateBackupCodesAsync(email, actorUserId, ct);
        if (!codesResult.Success || codesResult.Codes is not { Count: > 0 })
        {
            return new WorkspaceRecoveryCredentialsResult(
                Success: true,
                Email: email,
                TempPassword: resetResult.TemporaryPassword,
                BackupCode: null,
                Message: $"Password reset for {email}. Backup-code generation failed — "
                       + (codesResult.ErrorMessage
                            ?? "deliver the password only and ask the human to re-attempt the 2FA request out-of-band."));
        }

        return new WorkspaceRecoveryCredentialsResult(
            Success: true,
            Email: email,
            TempPassword: resetResult.TemporaryPassword,
            BackupCode: codesResult.Codes[0],
            Message: $"Password reset and one backup code issued for {email}. Deliver both securely — neither can be retrieved again.");
    }

}
