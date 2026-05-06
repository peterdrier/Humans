using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Authorization;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin")]
public class ProfileAdminController : HumansControllerBase
{
    private readonly IEmailProblemsService _emailProblems;
    private readonly IAccountMergeService _accountMerge;
    private readonly IUserEmailService _userEmails;
    private readonly IUserService _users;
    private readonly IAuditLogService _audit;
    private readonly ILogger<ProfileAdminController> _logger;

    public ProfileAdminController(
        UserManager<User> userManager,
        IEmailProblemsService emailProblems,
        IAccountMergeService accountMerge,
        IUserEmailService userEmails,
        IUserService users,
        IAuditLogService audit,
        ILogger<ProfileAdminController> logger)
        : base(userManager)
    {
        _emailProblems = emailProblems;
        _accountMerge = accountMerge;
        _userEmails = userEmails;
        _users = users;
        _audit = audit;
        _logger = logger;
    }
}
