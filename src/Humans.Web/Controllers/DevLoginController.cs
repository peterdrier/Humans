using System.Reflection;
using System.Text;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Configuration;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

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
///
/// Per design-rules §2a/§2c: this controller does not inject
/// <see cref="HumansDbContext"/>. Persona seeding (User + Profile + UserEmail
/// + auxiliary teams/camps/roles) is delegated to <see cref="DevPersonaSeeder"/>;
/// user lookup goes through <see cref="UserManager{TUser}"/> and
/// <see cref="IUserEmailService"/>.
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
    private readonly IUserEmailService _userEmailService;
    private readonly DevPersonaSeeder _personaSeeder;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ConfigurationRegistry _configRegistry;
    private readonly ILogger<DevLoginController> _logger;

    public DevLoginController(
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IUserEmailService userEmailService,
        DevPersonaSeeder personaSeeder,
        IWebHostEnvironment env,
        IConfiguration config,
        ConfigurationRegistry configRegistry,
        ILogger<DevLoginController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _userEmailService = userEmailService;
        _personaSeeder = personaSeeder;
        _env = env;
        _config = config;
        _configRegistry = configRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Signs in as a dev persona by slug (e.g., "admin", "noinfo-admin", "volunteer").
    /// </summary>
    [HttpGet("{persona}")]
    public async Task<IActionResult> SignIn(string persona, string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var info = AllPersonas.FirstOrDefault(p =>
            string.Equals(p.Slug, persona, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return NotFound();

        // Guest is non-deterministic: mint a brand-new profileless user every
        // click so multiple testers can onboard in parallel without colliding
        // on a single shared Guest account.
        if (string.Equals(info.Slug, "guest", StringComparison.OrdinalIgnoreCase))
            return await SignInAsFreshGuestAsync(info, returnUrl);

        var id = DevPersonaSeeder.PersonaGuid(info.Slug);
        Guid resolvedUserId;

        await SeedLock.WaitAsync();
        try
        {
            resolvedUserId = await _personaSeeder.EnsurePersonaAsync(info.Slug, info.DisplayName, id);
            if (string.Equals(info.Slug, "coordinator", StringComparison.OrdinalIgnoreCase))
                await _personaSeeder.EnsureCoordinatorTeamsAsync(resolvedUserId);
            if (DevPersonaSeeder.IsBarrioLeadSlug(info.Slug))
                await _personaSeeder.EnsureBarrioCampAsync(info.Slug, resolvedUserId);
            if (DevPersonaSeeder.IsCityPlanningSlug(info.Slug))
                await _personaSeeder.EnsureCityPlanningTeamAsync(resolvedUserId);
        }
        finally
        {
            SeedLock.Release();
        }

        var email = $"dev-{info.Slug}@localhost";
        var user = await _userManager.FindByIdAsync(resolvedUserId.ToString());
        if (user is null)
        {
            var byEmailUserId = await _userEmailService.GetUserIdByVerifiedEmailAsync(email);
            if (byEmailUserId is not null)
                user = await _userManager.FindByIdAsync(byEmailUserId.Value.ToString());
        }
        if (user is null)
        {
            _logger.LogError("Dev persona {Slug} ({Id}) not found after seeding", info.Slug, resolvedUserId);
            return StatusCode(500, "Dev persona seeding failed");
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogWarning("DEV LOGIN: signed in as {Email} ({Id})", user.Email, user.Id);

        return RedirectToLocalOrHome(returnUrl);
    }

    /// <summary>
    /// Shows a list of all real users in the database for sign-in.
    /// Useful in preview environments with cloned production-like data.
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> Users(string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var users = await _personaSeeder.GetUsersForChooserAsync();

        ViewData["ReturnUrl"] = returnUrl;
        return View(users.ToList());
    }

    /// <summary>
    /// Signs in as any user by ID. Used by the user chooser.
    /// </summary>
    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> SignInAsUser(Guid id, string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var user = await _userManager.FindByIdAsync(id.ToString());
        if (user is null)
            return NotFound();

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogWarning("DEV LOGIN: signed in as {Email} ({Id})", user.Email, user.Id);

        return RedirectToLocalOrHome(returnUrl);
    }

    private IActionResult RedirectToLocalOrHome(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl!)
            : RedirectToAction(nameof(HomeController.Index), "Home");

    private bool IsDevAuthEnabled()
    {
        if (_env.IsProduction())
            return false;

        return _config.GetSettingValue(
            _configRegistry, "DevAuth:Enabled", "Development", defaultValue: false);
    }

    /// <summary>
    /// Mints a brand-new profileless guest user via the seeder and signs in as
    /// them. Each click of the Guest button creates a fresh account so multiple
    /// testers can run the onboarding flow in parallel.
    /// </summary>
    private async Task<IActionResult> SignInAsFreshGuestAsync(DevPersonaInfo info, string? returnUrl)
    {
        var newId = await _personaSeeder.EnsureFreshGuestAsync(info.DisplayName);

        var user = await _userManager.FindByIdAsync(newId.ToString());
        if (user is null)
        {
            _logger.LogError("Fresh guest persona ({Id}) not found after seeding", newId);
            return StatusCode(500, "Dev guest seeding failed");
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogWarning("DEV LOGIN: signed in as fresh guest {Email} ({Id})", user.Email, user.Id);
        return RedirectToLocalOrHome(returnUrl);
    }

    // ============================================================
    // Static helpers
    // ============================================================

    private static List<DevPersonaInfo> BuildPersonaList()
    {
        var list = new List<DevPersonaInfo>
        {
            new("guest", "Guest (No Profile)"),
            new("volunteer", "Volunteer"),
            new("barrio-1-lead", "Barrio 1 Lead"),
            new("barrio-2-lead", "Barrio 2 Lead"),
            new("coordinator", "Coordinator"),
            new("city-planning", "City Planning Team")
        };

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
