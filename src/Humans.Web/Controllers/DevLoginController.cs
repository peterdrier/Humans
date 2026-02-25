#if DEBUG
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Web.Controllers;

/// <summary>
/// Development-only controller for signing in as pre-seeded personas without Google OAuth.
/// Cannot be used in production — two independent guards prevent it:
/// 1. <c>#if DEBUG</c> preprocessor directive excludes the entire class from Release builds.
/// 2. <c>_env.IsDevelopment()</c> runtime check returns 404 unless the environment is Development.
/// Both guards would need to be bypassed simultaneously for this to be exploitable.
/// </summary>
[Route("dev/login")]
public class DevLoginController : Controller
{
    // Fixed GUIDs so re-seeding is idempotent across restarts
    private static readonly Guid VolunteerId   = Guid.Parse("ddddddd0-0000-0000-0000-000000000001");
    private static readonly Guid AdminId       = Guid.Parse("ddddddd0-0000-0000-0000-000000000002");
    private static readonly Guid BoardId       = Guid.Parse("ddddddd0-0000-0000-0000-000000000003");

    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly HumansDbContext _db;
    private readonly IClock _clock;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DevLoginController> _logger;

    public DevLoginController(
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        HumansDbContext db,
        IClock clock,
        IWebHostEnvironment env,
        ILogger<DevLoginController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _clock = clock;
        _env = env;
        _logger = logger;
    }

    [HttpGet("volunteer")]
    public Task<IActionResult> Volunteer() => SignInAs(VolunteerId);

    [HttpGet("admin")]
    public Task<IActionResult> Admin() => SignInAs(AdminId);

    [HttpGet("board")]
    public Task<IActionResult> Board() => SignInAs(BoardId);

    private async Task<IActionResult> SignInAs(Guid personaId)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        await EnsurePersonasSeededAsync();

        var user = await _userManager.FindByIdAsync(personaId.ToString());
        if (user == null)
        {
            _logger.LogError("Dev persona {Id} not found after seeding", personaId);
            return StatusCode(500, "Dev persona seeding failed");
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogWarning("DEV LOGIN: signed in as {Email} ({Id})", user.Email, user.Id);

        return RedirectToAction("Index", "Home");
    }

    private async Task EnsurePersonasSeededAsync()
    {
        var now = _clock.GetCurrentInstant();

        await EnsureUserAsync(VolunteerId, "dev-volunteer@localhost", "Dev Volunteer", now);
        await EnsureVolunteerTeamMemberAsync(VolunteerId, now);

        await EnsureUserAsync(AdminId, "dev-admin@localhost", "Dev Admin", now);
        await EnsureVolunteerTeamMemberAsync(AdminId, now);
        await EnsureRoleAssignmentAsync(AdminId, RoleNames.Admin, now);

        await EnsureUserAsync(BoardId, "dev-board@localhost", "Dev Board", now);
        await EnsureVolunteerTeamMemberAsync(BoardId, now);
        await EnsureRoleAssignmentAsync(BoardId, RoleNames.Board, now);
    }

    private async Task EnsureUserAsync(Guid id, string email, string displayName, Instant now)
    {
        var existing = await _userManager.FindByIdAsync(id.ToString());
        if (existing != null)
            return;

        var user = new User
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("Failed to create dev persona {Email}: {Errors}",
                email, string.Join(", ", result.Errors.Select(e => e.Description)));
            return;
        }

        // Add a UserEmail record (required by the data model)
        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Email = email,
            IsOAuth = false,
            IsVerified = true,
            IsNotificationTarget = true,
            Visibility = ContactFieldVisibility.BoardOnly,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.UserEmails.Add(userEmail);
        await _db.SaveChangesAsync();

        _logger.LogInformation("DEV: seeded user {Email}", email);
    }

    private async Task EnsureVolunteerTeamMemberAsync(Guid userId, Instant now)
    {
        var exists = await _db.TeamMembers
            .AnyAsync(tm => tm.UserId == userId && tm.TeamId == SystemTeamIds.Volunteers && !tm.LeftAt.HasValue);

        if (exists)
            return;

        _db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TeamId = SystemTeamIds.Volunteers,
            Role = TeamMemberRole.Member,
            JoinedAt = now
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation("DEV: added user {UserId} to Volunteers team", userId);
    }

    private async Task EnsureRoleAssignmentAsync(Guid userId, string roleName, Instant now)
    {
        var exists = await _db.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == roleName &&
                (ra.ValidTo == null || ra.ValidTo > now));

        if (exists)
            return;

        _db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = now,
            ValidTo = null,
            Notes = "Dev persona — auto-seeded",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        await _db.SaveChangesAsync();

        _logger.LogInformation("DEV: assigned role {Role} to user {UserId}", roleName, userId);
    }
}
#endif
