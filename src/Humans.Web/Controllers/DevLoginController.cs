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
/// Development/preview controller for signing in without Google OAuth.
/// Guarded by TWO independent checks — both must pass:
/// 1. <c>DevAuth:Enabled</c> configuration value must be "true".
/// 2. Environment must NOT be "Production".
/// Set <c>DevAuth__Enabled=true</c> in preview/dev environments only.
/// </summary>
[Route("dev/login")]
public class DevLoginController : Controller
{
    // Fixed GUIDs so re-seeding is idempotent across restarts
    private static readonly Guid VolunteerId = Guid.Parse("ddddddd0-0000-0000-0000-000000000001");
    private static readonly Guid AdminId = Guid.Parse("ddddddd0-0000-0000-0000-000000000002");
    private static readonly Guid BoardId = Guid.Parse("ddddddd0-0000-0000-0000-000000000003");
    private static readonly Guid ConsentCoordId = Guid.Parse("ddddddd0-0000-0000-0000-000000000004");
    private static readonly Guid VolunteerCoordId = Guid.Parse("ddddddd0-0000-0000-0000-000000000005");

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly HumansDbContext _db;
    private readonly IClock _clock;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<DevLoginController> _logger;

    public DevLoginController(
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        HumansDbContext db,
        IClock clock,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<DevLoginController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _clock = clock;
        _env = env;
        _config = config;
        _logger = logger;
    }

    [HttpGet("volunteer")]
    public Task<IActionResult> Volunteer() => SignInAsPersona(VolunteerId);

    [HttpGet("admin")]
    public Task<IActionResult> Admin() => SignInAsPersona(AdminId);

    [HttpGet("board")]
    public Task<IActionResult> Board() => SignInAsPersona(BoardId);

    [HttpGet("consent-coordinator")]
    public Task<IActionResult> ConsentCoordinator() => SignInAsPersona(ConsentCoordId);

    [HttpGet("volunteer-coordinator")]
    public Task<IActionResult> VolunteerCoordinator() => SignInAsPersona(VolunteerCoordId);

    /// <summary>
    /// Shows a list of all real users in the database for sign-in.
    /// Useful in preview environments with cloned production-like data.
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var users = await _db.Users
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .Take(100)
            .ToListAsync();

        return View(users.Select(u => (u.Id, u.DisplayName ?? u.Email ?? "Unknown", u.Email ?? "")).ToList());
    }

    /// <summary>
    /// Signs in as any user by ID. Used by the user chooser.
    /// </summary>
    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> SignInAsUser(Guid id)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var user = await _userManager.FindByIdAsync(id.ToString());
        if (user == null)
            return NotFound();

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogWarning("DEV LOGIN: signed in as {Email} ({Id})", user.Email, user.Id);

        return RedirectToAction("Index", "Home");
    }

    private async Task<IActionResult> SignInAsPersona(Guid personaId)
    {
        if (!IsDevAuthEnabled())
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

    private bool IsDevAuthEnabled()
    {
        if (_env.IsProduction())
            return false;

        return _config.GetValue<bool>("DevAuth:Enabled");
    }

    private async Task EnsurePersonasSeededAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            var now = _clock.GetCurrentInstant();

            await EnsurePersonaAsync(VolunteerId, "dev-volunteer@localhost", "Dev", "Volunteer",
                now, [], [SystemTeamIds.Volunteers]);

            await EnsurePersonaAsync(AdminId, "dev-admin@localhost", "Dev", "Admin",
                now, [RoleNames.Admin], [SystemTeamIds.Volunteers]);

            await EnsurePersonaAsync(BoardId, "dev-board@localhost", "Dev", "Board",
                now, [RoleNames.Board], [SystemTeamIds.Volunteers, SystemTeamIds.Board]);

            await EnsurePersonaAsync(ConsentCoordId, "dev-consent@localhost", "Dev", "ConsentCoord",
                now, [RoleNames.ConsentCoordinator], [SystemTeamIds.Volunteers]);

            await EnsurePersonaAsync(VolunteerCoordId, "dev-volcoord@localhost", "Dev", "VolunteerCoord",
                now, [RoleNames.VolunteerCoordinator], [SystemTeamIds.Volunteers]);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private async Task EnsurePersonaAsync(
        Guid id, string email, string firstName, string lastName,
        Instant now, string[] roles, Guid[] teams)
    {
        var existing = await _userManager.FindByIdAsync(id.ToString());
        if (existing != null)
            return;

        var displayName = $"{firstName} {lastName}";

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

        // Batch remaining entities into a single SaveChangesAsync
        _db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Email = email,
            IsOAuth = true,
            IsVerified = true,
            IsNotificationTarget = true,
            Visibility = ContactFieldVisibility.BoardOnly,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        _db.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = id,
            BurnerName = displayName,
            FirstName = firstName,
            LastName = lastName,
            IsApproved = true,
            ConsentCheckStatus = ConsentCheckStatus.Cleared,
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var teamId in teams)
        {
            _db.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(),
                UserId = id,
                TeamId = teamId,
                Role = TeamMemberRole.Member,
                JoinedAt = now
            });
        }

        foreach (var roleName in roles)
        {
            _db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(),
                UserId = id,
                RoleName = roleName,
                ValidFrom = now,
                ValidTo = null,
                Notes = "Dev persona — auto-seeded",
                CreatedAt = now,
                CreatedByUserId = id
            });
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("DEV: seeded persona {Email} with roles [{Roles}] and teams [{Teams}]",
            email, string.Join(", ", roles), string.Join(", ", teams));
    }
}
