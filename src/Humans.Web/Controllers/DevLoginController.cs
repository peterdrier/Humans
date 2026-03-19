using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
/// Info about a dev login persona, exposed for the login view.
/// </summary>
public record DevPersonaInfo(string Slug, string DisplayName);

/// <summary>
/// Development/preview controller for signing in without Google OAuth.
/// Guarded by TWO independent checks — both must pass:
/// 1. <c>DevAuth:Enabled</c> configuration value must be "true".
/// 2. Environment must NOT be "Production".
/// Set <c>DevAuth__Enabled=true</c> in preview/dev environments only.
///
/// Personas are dynamically generated from <see cref="RoleNames"/> constants
/// so new roles automatically get a dev login button.
/// </summary>
[Route("dev/login")]
public class DevLoginController : Controller
{
    /// <summary>
    /// All available dev personas: Volunteer (no role) + one per RoleNames constant.
    /// Referenced by Login.cshtml to render buttons dynamically.
    /// </summary>
    public static IReadOnlyList<DevPersonaInfo> AllPersonas { get; } = BuildPersonaList();

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

    /// <summary>
    /// Signs in as a dev persona by slug (e.g., "admin", "noinfo-admin", "volunteer").
    /// </summary>
    [HttpGet("{persona}")]
    public async Task<IActionResult> SignIn(string persona)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var info = AllPersonas.FirstOrDefault(p =>
            string.Equals(p.Slug, persona, StringComparison.OrdinalIgnoreCase));
        if (info == null)
            return NotFound();

        var id = PersonaGuid(info.Slug);

        await SeedLock.WaitAsync();
        try
        {
            await EnsurePersonaAsync(info, id);
        }
        finally
        {
            SeedLock.Release();
        }

        var email = $"dev-{info.Slug}@localhost";
        var user = await _userManager.FindByIdAsync(id.ToString())
                   ?? await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            _logger.LogError("Dev persona {Slug} ({Id}) not found after seeding", info.Slug, id);
            return StatusCode(500, "Dev persona seeding failed");
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogWarning("DEV LOGIN: signed in as {Email} ({Id})", user.Email, user.Id);

        return RedirectToAction("Index", "Home");
    }

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

    private bool IsDevAuthEnabled()
    {
        if (_env.IsProduction())
            return false;

        return _config.GetValue<bool>("DevAuth:Enabled");
    }

    private async Task EnsurePersonaAsync(DevPersonaInfo info, Guid id)
    {
        var existing = await _userManager.FindByIdAsync(id.ToString());
        if (existing != null)
            return;

        var email = $"dev-{info.Slug}@localhost";

        // Legacy personas may exist with old hardcoded GUIDs — reuse them
        var byEmail = await _userManager.FindByEmailAsync(email);
        if (byEmail != null)
        {
            _logger.LogInformation("DEV: found legacy persona {Email} ({OldId}), reusing", email, byEmail.Id);
            return;
        }

        var now = _clock.GetCurrentInstant();
        var displayName = $"Dev {info.DisplayName}";
        var nameParts = info.DisplayName.Split(' ', 2);
        var firstName = nameParts[0];
        var lastName = nameParts.Length > 1 ? nameParts[1] : info.DisplayName;

        // Determine role and team assignments
        var roleName = RoleNameFromSlug(info.Slug);
        var roles = roleName != null ? new[] { roleName } : Array.Empty<string>();
        var teams = roleName != null && string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal)
            ? new[] { SystemTeamIds.Volunteers, SystemTeamIds.Board }
            : new[] { SystemTeamIds.Volunteers };

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

        foreach (var role in roles)
        {
            _db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(),
                UserId = id,
                RoleName = role,
                ValidFrom = now,
                ValidTo = null,
                Notes = "Dev persona — auto-seeded",
                CreatedAt = now,
                CreatedByUserId = id
            });
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("DEV: seeded persona {Email} with roles [{Roles}] and teams [{Teams}]",
            email, string.Join(", ", roles), string.Join(", ", teams.Select(t => t)));
    }

    // ============================================================
    // Static helpers
    // ============================================================

    private static List<DevPersonaInfo> BuildPersonaList()
    {
        var list = new List<DevPersonaInfo> { new("volunteer", "Volunteer") };

        var roles = typeof(RoleNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(r => r, StringComparer.Ordinal);

        foreach (var role in roles)
        {
            list.Add(new(PascalToKebab(role), PascalToDisplay(role)));
        }

        return list;
    }

    /// <summary>
    /// Deterministic GUID from persona slug — stable across restarts for idempotent seeding.
    /// </summary>
    private static Guid PersonaGuid(string slug)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"dev-persona:{slug}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Resolves a persona slug back to a RoleNames constant, or null for "volunteer".
    /// </summary>
    private static string? RoleNameFromSlug(string slug)
    {
        if (string.Equals(slug, "volunteer", StringComparison.OrdinalIgnoreCase))
            return null;

        return typeof(RoleNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .FirstOrDefault(r => string.Equals(PascalToKebab(r), slug, StringComparison.OrdinalIgnoreCase));
    }

    private static string PascalToKebab(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    private static string PascalToDisplay(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
                sb.Append(' ');
            sb.Append(pascal[i]);
        }
        return sb.ToString();
    }
}
