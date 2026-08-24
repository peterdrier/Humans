using System.Reflection;
using System.Text;
using Humans.Base.Configuration;
using Humans.Auth.Contracts;
using Humans.Users.Contracts;
using Humans.Development.Services;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Development.Controllers;

internal sealed record DevPersonaInfo(string Slug, string DisplayName);

// Dev/preview sign-in. Gated by DevAuth:Enabled=true AND non-Production env. Personas from RoleNames.
[Route("dev/login")]
internal sealed class DevLoginController(
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    IUserEmailService userEmailService,
    DevPersonaSeeder personaSeeder,
    IRoleAssignmentService roleAssignmentService,
    IWebHostEnvironment env,
    IConfiguration config,
    ConfigurationRegistry configRegistry,
    ILogger<DevLoginController> logger) : Controller
{
    // Rendered by the section's own Views/Shared/_DevLoginPanel.cshtml, which Shell's
    // /Account/Login pulls in by name across application parts. It used to be public because
    // Shell's view named the type directly; the markup moved instead (step 3b), so the persona
    // list never leaves the section.
    internal static IReadOnlyList<DevPersonaInfo> AllPersonas { get; } = BuildPersonaList();

    /// <summary>
    /// The personas offered on this host. Admin is dropped unless the host allows it — see
    /// <see cref="AdminSignInAllowed"/>. Used by the panel so the buttons and the route agree
    /// on one predicate.
    /// </summary>
    internal static IEnumerable<DevPersonaInfo> PersonasFor(
        IWebHostEnvironment environment, IConfiguration configuration) =>
        AdminSignInAllowed(environment, configuration)
            ? AllPersonas
            : AllPersonas.Where(p => !IsAdminPersona(p));

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

        // Before any seeding: an Admin session must never be minted off an anonymous URL
        // outside a dev host.
        if (IsAdminPersona(info) && !AdminSignInAllowed(env, config))
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

        await signInManager.SignInAsync(user, isPersistent: true);
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
            // No-Name persona: re-blank the legal names on every sign-in so the
            // onboarding name-gate (#812) re-triggers each time it's used.
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

        // Impersonating a real Admin is the same hole as the Admin persona.
        if (!AdminSignInAllowed(env, config) && await roleAssignmentService.IsUserAdminAsync(id))
            return NotFound();

        await signInManager.SignInAsync(user, isPersistent: true);
        logger.LogWarning("DEV LOGIN: signed in as user {Id}", user.Id);

        return RedirectToLocalOrHome(returnUrl);
    }

    private IActionResult RedirectToLocalOrHome(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
            // "Index" as a literal: HomeController is Shell's and a section cannot name it
            // (step 5). Covered by DevelopmentPageRenderTests, which follows the redirect.
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

        var user = await userManager.FindByIdAsync(newId.ToString());
        if (user is null)
        {
            logger.LogError("Fresh guest persona ({Id}) not found after seeding", newId);
            return StatusCode(500, "Dev guest seeding failed");
        }

        await signInManager.SignInAsync(user, isPersistent: true);
        logger.LogWarning("DEV LOGIN: signed in as fresh guest {Id}", user.Id);
        return RedirectToLocalOrHome(returnUrl);
    }

    // --- Static helpers ---

    /// <summary>
    /// Whether this host may hand out an Admin session through dev login. QA runs Staging with
    /// <c>DevAuth:Enabled</c> on and real Google Workspace data, so anonymous Admin there is a
    /// live privilege escalation. "Testing" is the in-process integration host, the same
    /// discriminator Program.cs uses. <c>DevAuth:AllowAdmin</c> opts a host back in: per-PR
    /// previews set it from <c>docker-entrypoint.sh</c>, which is the only place that can tell
    /// a preview container from QA — both run Staging, but a preview holds a throwaway cloned
    /// database and no integration credentials worth escalating to.
    /// </summary>
    private static bool AdminSignInAllowed(IWebHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || environment.IsEnvironment("Testing")
        || configuration.GetValue("DevAuth:AllowAdmin", false);

    /// <summary>The persona whose seeded governance role is <see cref="RoleNames.Admin"/>.</summary>
    private static bool IsAdminPersona(DevPersonaInfo persona) =>
        string.Equals(
            DevPersonaSeeder.RoleNameFromSlug(persona.Slug), RoleNames.Admin, StringComparison.Ordinal);

    private static List<DevPersonaInfo> BuildPersonaList()
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

        var roles = typeof(RoleNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(r => r, StringComparer.Ordinal);

        foreach (var role in roles)
        {
            list.Add(new(DevPersonaSeeder.PascalToKebab(role), PascalToDisplay(role)));
        }

        return list;
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
