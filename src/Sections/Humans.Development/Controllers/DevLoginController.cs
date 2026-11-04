using System.Text;
using Humans.Base.Configuration;
using Humans.Auth.Contracts;
using Humans.Users.Contracts;
using Humans.Development.Services;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Development.Controllers;

internal sealed record DevPersonaInfo(string Slug, string DisplayName);

// Dev/preview sign-in. Gated by DevAuth:Enabled=true AND non-Production env. One persona per catalogued role.
[Route("dev/login")]
[AllowAnonymous]
internal sealed class DevLoginController(
    IUserSignInService signIn,
    IUserEmailService userEmailService,
    DevPersonaSeeder personaSeeder,
    IRoleAssignmentService roleAssignmentService,
    IWebHostEnvironment env,
    IConfiguration config,
    ConfigurationRegistry configRegistry,
    RoleCatalog catalog,
    ILogger<DevLoginController> logger) : Controller
{
    // Rendered by Views/Shared/_DevLoginPanel.cshtml, which Shell's /Account/Login pulls in by name.
    internal static IReadOnlyList<DevPersonaInfo> AllPersonas(RoleCatalog catalog) => BuildPersonaList(catalog);

    /// <summary>
    /// Drops Admin unless <see cref="AdminSignInAllowed"/>. The panel calls this so the buttons and the route share
    /// one predicate.
    /// </summary>
    internal static IEnumerable<DevPersonaInfo> PersonasFor(
        IWebHostEnvironment environment, IConfiguration configuration, RoleCatalog catalog) =>
        AdminSignInAllowed(environment, configuration)
            ? AllPersonas(catalog)
            : AllPersonas(catalog).Where(p => !IsAdminPersona(p));

    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    [HttpGet("{persona}")]
    public async Task<IActionResult> SignIn(string persona, string? returnUrl = null)
    {
        if (!IsDevAuthEnabled())
            return NotFound();

        var info = AllPersonas(catalog).FirstOrDefault(p =>
            string.Equals(p.Slug, persona, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return NotFound();

        // Before any seeding: an Admin session must never be minted off an anonymous URL
        // outside a dev host.
        if (IsAdminPersona(info) && !AdminSignInAllowed(env, config))
            return NotFound();

        // Guest: fresh profileless user per click so parallel testers don't collide.
        if (string.Equals(info.Slug, "guest", StringComparison.OrdinalIgnoreCase))
            return await SignInAsFreshGuestAsync(info, returnUrl);

        var resolvedUserId = await SeedPersonaAsync(info);
        var signedInId = await SignInPersonaAsync(info, resolvedUserId);
        if (signedInId is null)
        {
            logger.LogError("Dev persona {Slug} ({Id}) not found after seeding", info.Slug, resolvedUserId);
            return StatusCode(500, "Dev persona seeding failed");
        }

        logger.LogWarning("DEV LOGIN: signed in as user {Id}", signedInId.Value);

        return RedirectToLocalOrHome(returnUrl);
    }

    private async Task<Guid> SeedPersonaAsync(DevPersonaInfo info)
    {
        var id = DevPersonaSeeder.PersonaGuid(info.Slug);
        Guid resolvedUserId;

        await SeedLock.WaitAsync();
        try
        {
            resolvedUserId = await personaSeeder.EnsurePersonaAsync(info.Slug, info.DisplayName, id);
            // Re-blank the legal names on every sign-in so the onboarding name gate re-triggers.
            if (string.Equals(info.Slug, "no-name", StringComparison.OrdinalIgnoreCase))
                await personaSeeder.ResetLegalNamesAsync(resolvedUserId);
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

        return resolvedUserId;
    }

    /// <summary>Falls back to the persona's seeded address when the resolved id has no account.</summary>
    private async Task<Guid?> SignInPersonaAsync(DevPersonaInfo info, Guid resolvedUserId)
    {
        if (await signIn.SignInAsync(resolvedUserId))
            return resolvedUserId;

        var email = $"dev-{info.Slug}@localhost";
        var byEmailUserId = (await userEmailService.FindByAddressAsync(email, aliased: false, verifiedOnly: true))
            .FirstOrDefault()?.UserId;

        return byEmailUserId is { } id && await signIn.SignInAsync(id) ? id : null;
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

        // Impersonating a real Admin is the same hole as the Admin persona.
        if (!AdminSignInAllowed(env, config) && await roleAssignmentService.IsUserAdminAsync(id))
            return NotFound();

        if (!await signIn.SignInAsync(id))
            return NotFound();
        logger.LogWarning("DEV LOGIN: signed in as user {Id}", id);

        return RedirectToLocalOrHome(returnUrl);
    }

    private IActionResult RedirectToLocalOrHome(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
            // "Index" as a literal: HomeController is Shell's and a section cannot name it.
            // Covered by DevelopmentPageRenderTests, which follows the redirect.
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");

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

        if (!await signIn.SignInAsync(newId))
        {
            logger.LogError("Fresh guest persona ({Id}) not found after seeding", newId);
            return StatusCode(500, "Dev guest seeding failed");
        }

        logger.LogWarning("DEV LOGIN: signed in as fresh guest {Id}", newId);
        return RedirectToLocalOrHome(returnUrl);
    }

    /// <summary>
    /// QA runs Staging with <c>DevAuth:Enabled</c> and real Google Workspace data, so anonymous Admin there is a live
    /// privilege escalation. <c>DevAuth:AllowAdmin</c> opts a host back in: only <c>docker-entrypoint.sh</c> can tell
    /// a per-PR preview (throwaway cloned database) from QA, and it sets the flag.
    /// </summary>
    private static bool AdminSignInAllowed(IWebHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || environment.IsEnvironment("Testing")
        || configuration.GetValue("DevAuth:AllowAdmin", false);

    private static bool IsAdminPersona(DevPersonaInfo persona) =>
        string.Equals(persona.Slug, DevPersonaSeeder.PascalToKebab(RoleNames.Admin), StringComparison.OrdinalIgnoreCase);

    private static List<DevPersonaInfo> BuildPersonaList(RoleCatalog catalog)
    {
        var list = new List<DevPersonaInfo>
        {
            new("guest", "Guest (No Profile)"),
            new("no-name", "No Name (gate test)"),
            new("volunteer", "Volunteer"),
            new("barrio-1-lead", "Barrio 1 Lead"),
            new("barrio-2-lead", "Barrio 2 Lead"),
            new("coordinator", "Coordinator"),
            new("city-planning", "City Planning Team")
        };

        foreach (var role in catalog.Roles.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            list.Add(new(DevPersonaSeeder.PascalToKebab(role.Name), role.Title));
        }

        return list;
    }
}
