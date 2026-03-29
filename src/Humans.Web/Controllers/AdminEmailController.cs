using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Helpers;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleNames.Admin)]
[Route("Admin/Email")]
public class AdminEmailController : HumansControllerBase
{
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<AdminEmailController> _logger;

    private const string NobodiesTeamDomain = "nobodies.team";

    public AdminEmailController(
        UserManager<User> userManager,
        IGoogleWorkspaceUserService workspaceUserService,
        IUserEmailService userEmailService,
        IAuditLogService auditLogService,
        HumansDbContext dbContext,
        ILogger<AdminEmailController> logger)
        : base(userManager)
    {
        _workspaceUserService = workspaceUserService;
        _userEmailService = userEmailService;
        _auditLogService = auditLogService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            var accounts = await _workspaceUserService.ListAccountsAsync();

            // Load all user emails to match accounts to humans
            var allUserEmails = await _dbContext.UserEmails
                .AsNoTracking()
                .Include(ue => ue.User)
                .ToListAsync();

            var accountViewModels = new List<WorkspaceEmailAccountViewModel>();
            var notPrimaryCount = 0;

            foreach (var account in accounts)
            {
                var matchedEmail = allUserEmails.FirstOrDefault(ue =>
                    string.Equals(ue.Email, account.PrimaryEmail, StringComparison.OrdinalIgnoreCase));

                var isUsedAsPrimary = matchedEmail is { IsNotificationTarget: true };

                // Count accounts that exist in the system but are not used as primary
                if (matchedEmail is not null && !isUsedAsPrimary)
                {
                    notPrimaryCount++;
                }

                accountViewModels.Add(new WorkspaceEmailAccountViewModel
                {
                    PrimaryEmail = account.PrimaryEmail,
                    FirstName = account.FirstName,
                    LastName = account.LastName,
                    IsSuspended = account.IsSuspended,
                    CreationTime = account.CreationTime,
                    LastLoginTime = account.LastLoginTime,
                    MatchedUserId = matchedEmail?.UserId,
                    MatchedDisplayName = matchedEmail?.User?.DisplayName,
                    IsUsedAsPrimary = isUsedAsPrimary
                });
            }

            var linkedCount = accountViewModels.Count(a => a.MatchedUserId.HasValue);

            var model = new WorkspaceEmailListViewModel
            {
                Accounts = accountViewModels
                    .OrderBy(a => a.PrimaryEmail, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                TotalAccounts = accountViewModels.Count,
                ActiveAccounts = accountViewModels.Count(a => !a.IsSuspended),
                SuspendedAccounts = accountViewModels.Count(a => a.IsSuspended),
                LinkedAccounts = linkedCount,
                UnlinkedAccounts = accountViewModels.Count - linkedCount,
                NotPrimaryCount = notPrimaryCount
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load @nobodies.team accounts");
            SetError("Failed to load @nobodies.team accounts. Check the logs for details.");
            return View(new WorkspaceEmailListViewModel());
        }
    }

    [HttpPost("Provision")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Provision(ProvisionWorkspaceAccountModel model)
    {
        if (string.IsNullOrWhiteSpace(model.EmailPrefix) ||
            string.IsNullOrWhiteSpace(model.FirstName) ||
            string.IsNullOrWhiteSpace(model.LastName))
        {
            SetError("All fields are required.");
            return RedirectToAction(nameof(Index));
        }

        var emailPrefix = model.EmailPrefix.Trim().ToLowerInvariant();
        var fullEmail = $"{emailPrefix}@{NobodiesTeamDomain}";

        // Check if account already exists
        var existing = await _workspaceUserService.GetAccountAsync(fullEmail);
        if (existing is not null)
        {
            SetError($"Account {fullEmail} already exists.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            // Generate a temporary password
            var tempPassword = PasswordGenerator.GenerateTemporary();

            await _workspaceUserService.ProvisionAccountAsync(
                fullEmail, model.FirstName.Trim(), model.LastName.Trim(), tempPassword);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountProvisioned,
                    "WorkspaceAccount", Guid.Empty,
                    $"Provisioned @{NobodiesTeamDomain} account: {fullEmail}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {fullEmail} provisioned. Temporary password: {tempPassword}");
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision @nobodies.team account: {Email}", fullEmail);
            SetError($"Failed to provision {fullEmail}. Check logs for details.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        try
        {
            await _workspaceUserService.SuspendAccountAsync(email);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountSuspended,
                    "WorkspaceAccount", Guid.Empty,
                    $"Suspended @{NobodiesTeamDomain} account: {email}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {email} suspended.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suspend account: {Email}", email);
            SetError($"Failed to suspend {email}.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reactivate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        try
        {
            await _workspaceUserService.ReactivateAccountAsync(email);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountReactivated,
                    "WorkspaceAccount", Guid.Empty,
                    $"Reactivated @{NobodiesTeamDomain} account: {email}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Account {email} reactivated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reactivate account: {Email}", email);
            SetError($"Failed to reactivate {email}.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ResetPassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest();
        }

        try
        {
            var newPassword = PasswordGenerator.GenerateTemporary();
            await _workspaceUserService.ResetPasswordAsync(email, newPassword);

            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountPasswordReset,
                    "WorkspaceAccount", Guid.Empty,
                    $"Reset password for @{NobodiesTeamDomain} account: {email}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Password reset for {email}. New temporary password: {newPassword}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset password for: {Email}", email);
            SetError($"Failed to reset password for {email}.");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Link(string email, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(email) || userId == Guid.Empty)
        {
            SetError("Email and human are required.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var user = await _dbContext.Users.FindAsync(userId);
            if (user is null)
            {
                SetError("Human not found.");
                return RedirectToAction(nameof(Index));
            }

            // Check not already linked
            var alreadyLinked = await _dbContext.UserEmails
                .AnyAsync(ue => EF.Functions.ILike(ue.Email, email));
            if (alreadyLinked)
            {
                SetError($"{email} is already linked to a human.");
                return RedirectToAction(nameof(Index));
            }

            // Add as verified email (also sets notification target for @nobodies.team)
            await _userEmailService.AddVerifiedEmailAsync(userId, email);

            // Auto-set as Google service email
            user.GoogleEmail = email;
            await _dbContext.SaveChangesAsync();

            // Audit
            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                await _auditLogService.LogAsync(
                    AuditAction.WorkspaceAccountLinked,
                    "WorkspaceAccount", userId,
                    $"Linked @{NobodiesTeamDomain} account {email} to {user.DisplayName}",
                    currentUser.Id, currentUser.DisplayName);
                await _dbContext.SaveChangesAsync();
            }

            SetSuccess($"Linked {email} to {user.DisplayName}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link {Email} to user {UserId}", email, userId);
            SetError($"Failed to link {email}.");
        }

        return RedirectToAction(nameof(Index));
    }

}
