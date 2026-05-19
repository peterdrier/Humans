using System.Reflection;
using System.Text;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public record DevPersonaInfo(string Slug, string DisplayName);

// Dev/preview sign-in. Gated by DevAuth:Enabled=true AND non-Production env. Personas from RoleNames.
[Route("dev/login")]
public class DevLoginController(
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    IUserEmailService userEmailService,
    DevPersonaSeeder personaSeeder,
    IWebHostEnvironment env,
    IConfiguration config,
    ConfigurationRegistry configRegistry,
    ILogger<DevLoginController> logger) : Controller
{
    public static IReadOnlyList<DevPersonaInfo> AllPersonas { get; } = BuildPersonaList();

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    [HttpGet("{persona}")]
    public async Task<IActionResult> SignIn(string persona, string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var info = AllPersonas.FirstOrDefault(p =>
            string.Equals(p.Slug, persona, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return NotFound();

        // Guest: fresh profileless user per click so parallel testers don't collide.
        if (string.Equals(info.Slug, "guest", StringComparison.OrdinalIgnoreCase))
            return await SignInAsFreshGuestAsync(info, returnUrl);

        var (resolvedUserId, user) = await ResolveSeededPersonaUserAsync(info);
        if (user is null)
        {
            logger.LogError("Dev persona {Slug} ({Id}) not found after seeding", info.Slug, resolvedUserId);
            return StatusCode(500, "Dev persona seeding failed");
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        logger.LogWarning("DEV LOGIN: signed in as user {Id}", user.Id);

        return RedirectToLocalOrHome(returnUrl);
    }

    private async Task<(Guid UserId, User? User)> ResolveSeededPersonaUserAsync(DevPersonaInfo info)
    {
        var id = DevPersonaSeeder.PersonaGuid(info.Slug);
        Guid resolvedUserId;

        await SeedLock.WaitAsync();
        try
        {
            resolvedUserId = await personaSeeder.EnsurePersonaAsync(info.Slug, info.DisplayName, id);
            if (string.Equals(info.Slug, "coordinator", StringComparison.OrdinalIgnoreCase))
                await personaSeeder.EnsureCoordinatorTeamsAsync(resolvedUserId);
            if (DevPersonaSeeder.IsBarrioLeadSlug(info.Slug))
                await personaSeeder.EnsureBarrioCampAsync(info.Slug, resolvedUserId);
            if (DevPersonaSeeder.IsCityPlanningSlug(info.Slug))
                await personaSeeder.EnsureCityPlanningTeamAsync(resolvedUserId);
        }
        finally
        {
            SeedLock.Release();
        }

        var user = await userManager.FindByIdAsync(resolvedUserId.ToString());
        if (user is not null)
            return (resolvedUserId, user);

        var email = $"dev-{info.Slug}@localhost";
        var byEmailUserId = await userEmailService.GetUserIdByVerifiedEmailAsync(email);
        user = byEmailUserId is null
            ? null
            : await userManager.FindByIdAsync(byEmailUserId.Value.ToString());

        return (resolvedUserId, user);
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var users = await personaSeeder.GetUsersForChooserAsync();

        ViewData["ReturnUrl"] = returnUrl;
        return View(users.ToList());
    }

    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> SignInAsUser(Guid id, string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
            return NotFound();

        await signInManager.SignInAsync(user, isPersistent: false);
        logger.LogWarning("DEV LOGIN: signed in as user {Id}", user.Id);

        return RedirectToLocalOrHome(returnUrl);
    }

    private IActionResult RedirectToLocalOrHome(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl!)
            : RedirectToAction(nameof(HomeController.Index), "Home");

    private bool IsDevAuthEnabled()
    {
        if (env.IsProduction())
            return false;

        return config.GetSettingValue(
            configRegistry, "DevAuth:Enabled", "Development", defaultValue: false);
    }

    private async Task<IActionResult> SignInAsFreshGuestAsync(DevPersonaInfo info, string? returnUrl)
    {
        var newId = await personaSeeder.EnsureFreshGuestAsync(info.DisplayName);

        var user = await userManager.FindByIdAsync(newId.ToString());
        if (user is null)
        {
            logger.LogError("Fresh guest persona ({Id}) not found after seeding", newId);
            return StatusCode(500, "Dev guest seeding failed");
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        logger.LogWarning("DEV LOGIN: signed in as fresh guest {Id}", user.Id);
        return RedirectToLocalOrHome(returnUrl);
    }

    // --- Static helpers ---

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
