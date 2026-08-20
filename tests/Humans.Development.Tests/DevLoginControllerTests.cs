using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Consent.Contracts;
using Humans.Development.Controllers;
using Humans.Development.Services;
using Humans.Governance.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Development.Tests;

/// <summary>
/// Covers <c>Development.md</c>'s Admin lockdown: dev login may not hand out an Admin
/// session outside a dev host. QA runs as <c>Staging</c> with <c>DevAuth:Enabled</c> and
/// real Google Workspace data, so both the <c>admin</c> persona route and impersonation of
/// a human holding an active Admin assignment must 404 there. Per-PR previews run the same
/// <c>Staging</c> name but opt back in with <c>DevAuth:AllowAdmin</c>.
///
/// Same shape as <see cref="DevSeedControllerTests"/>: the environment gate runs inside the
/// action, so both branches are exercised through a substituted
/// <see cref="IWebHostEnvironment"/>.
/// </summary>
public class DevLoginControllerTests
{
    private readonly UserManager<User> _userManager = Substitute.For<UserManager<User>>(
        Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);

    private readonly SignInManager<User> _signInManager = Substitute.For<SignInManager<User>>(
        Substitute.For<UserManager<User>>(
            Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null),
        Substitute.For<IHttpContextAccessor>(),
        Substitute.For<IUserClaimsPrincipalFactory<User>>(),
        null, null, null, null);

    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IRoleAssignmentService _roleAssignments = Substitute.For<IRoleAssignmentService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IWebHostEnvironment _environment = Substitute.For<IWebHostEnvironment>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ConfigurationRegistry _configRegistry = new();

    public DevLoginControllerTests()
    {
        // QA and Production leave the opt-in unset. The substitute answers "" rather than null
        // for an unstubbed section, which no IConfiguration does, so state the off case.
        SetAllowAdmin(false);
    }

    [HumansFact]
    public async Task SignIn_AdminPersona_OutsideDevelopment_ReturnsNotFoundBeforeSeeding()
    {
        // QA's exact shape: Staging + DevAuth on. The refusal must land before the seeder
        // touches anything — an Admin user seeded into QA outlives the request.
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);

        var result = await BuildSut().SignIn("admin");

        result.Should().BeOfType<NotFoundResult>();
        await _userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<User>(), Arg.Any<bool>());
    }

    [HumansFact]
    public async Task SignIn_AdminPersona_InDevelopment_StillSignsIn()
    {
        _environment.EnvironmentName.Returns("Development");
        SetDevAuthEnabled(true);
        SeedExistingPersona("admin");

        var result = await BuildSut().SignIn("admin");

        result.Should().BeOfType<RedirectToActionResult>();
        await _signInManager.Received(1).SignInAsync(Arg.Any<User>(), true);
    }

    [HumansFact]
    public async Task SignIn_NonAdminPersona_OutsideDevelopment_StillSignsIn()
    {
        // The gate is Admin-only: dev login remains the way into QA for every other persona.
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);
        SeedExistingPersona("volunteer");

        var result = await BuildSut().SignIn("volunteer");

        result.Should().BeOfType<RedirectToActionResult>();
        await _signInManager.Received(1).SignInAsync(Arg.Any<User>(), true);
    }

    [HumansFact]
    public async Task SignIn_AdminPersona_PreviewWithAllowAdmin_SignsIn()
    {
        // A per-PR preview: Staging like QA, but docker-entrypoint.sh set DevAuth:AllowAdmin
        // because the container holds a throwaway cloned database and no real credentials.
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);
        SetAllowAdmin(true);
        SeedExistingPersona("admin");

        var result = await BuildSut().SignIn("admin");

        result.Should().BeOfType<RedirectToActionResult>();
        await _signInManager.Received(1).SignInAsync(Arg.Any<User>(), true);
    }

    [HumansFact]
    public async Task SignInAsUser_ActiveAdminAssignment_PreviewWithAllowAdmin_SignsIn()
    {
        // The chooser follows the same predicate, so a preview can also impersonate a real Admin.
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);
        SetAllowAdmin(true);
        var id = Guid.NewGuid();
        var user = new User { Id = id };
        _userManager.FindByIdAsync(id.ToString()).Returns(user);
        _roleAssignments.IsUserAdminAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await BuildSut().SignInAsUser(id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _signInManager.Received(1).SignInAsync(user, true);
    }

    [HumansFact]
    public async Task SignInAsUser_ActiveAdminAssignment_OutsideDevelopment_ReturnsNotFound()
    {
        // The chooser's second hole: any real Admin is reachable by id without the persona route.
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);
        var id = Guid.NewGuid();
        _userManager.FindByIdAsync(id.ToString()).Returns(new User { Id = id });
        _roleAssignments.IsUserAdminAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await BuildSut().SignInAsUser(id);

        result.Should().BeOfType<NotFoundResult>();
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<User>(), Arg.Any<bool>());
    }

    [HumansFact]
    public async Task SignInAsUser_NonAdmin_OutsideDevelopment_StillSignsIn()
    {
        _environment.EnvironmentName.Returns("Staging");
        SetDevAuthEnabled(true);
        var id = Guid.NewGuid();
        var user = new User { Id = id };
        _userManager.FindByIdAsync(id.ToString()).Returns(user);
        _roleAssignments.IsUserAdminAsync(id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await BuildSut().SignInAsUser(id);

        result.Should().BeOfType<RedirectToActionResult>();
        await _signInManager.Received(1).SignInAsync(user, true);
    }

    [HumansFact]
    public void PersonasFor_FollowsTheSamePredicateAsTheRoute()
    {
        // _DevLoginPanel renders this list; the button and the route must not disagree.
        _environment.EnvironmentName.Returns("Staging");

        DevLoginController.PersonasFor(_environment, _configuration)
            .Should().NotContain(p => p.Slug == "admin");

        SetAllowAdmin(true);

        DevLoginController.PersonasFor(_environment, _configuration)
            .Should().Contain(p => p.Slug == "admin");
    }

    /// <summary>
    /// Makes the persona resolve to an already-seeded user, so <c>EnsurePersonaAsync</c>
    /// short-circuits to the repair path and the action reaches the sign-in.
    /// </summary>
    private void SeedExistingPersona(string slug)
    {
        var id = DevPersonaSeeder.PersonaGuid(slug);
        _userManager.FindByIdAsync(id.ToString()).Returns(new User { Id = id });
    }

    private void SetDevAuthEnabled(bool enabled) => SetFlag("DevAuth:Enabled", "Enabled", enabled);

    /// <summary>A preview container's shape: Staging, dev auth on, and Admin opted back in.</summary>
    private void SetAllowAdmin(bool allowed) => SetFlag("DevAuth:AllowAdmin", "AllowAdmin", allowed);

    private void SetFlag(string path, string key, bool value)
    {
        var section = Substitute.For<IConfigurationSection>();
        section.Value.Returns(value ? "true" : "false");
        section.Path.Returns(path);
        section.Key.Returns(key);
        _configuration.GetSection(path).Returns(section);
        _configuration[path].Returns(value ? "true" : "false");
    }

    private DevLoginController BuildSut()
    {
        var ctrl = new DevLoginController(
            _userManager,
            _signInManager,
            _userEmails,
            BuildSeeder(),
            _roleAssignments,
            _environment,
            _configuration,
            _configRegistry,
            NullLogger<DevLoginController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            // RedirectToLocalOrHome consults Url.IsLocalUrl; the substitute answers false,
            // which is the no-returnUrl branch this suite exercises.
            Url = Substitute.For<IUrlHelper>(),
        };
        return ctrl;
    }

    private DevPersonaSeeder BuildSeeder() => new(
        _userManager,
        Substitute.For<IProfileEditorService>(),
        _userEmails,
        Substitute.For<IContactFieldService>(),
        _roleAssignments,
        Substitute.For<IUserInfoInvalidator>(),
        Substitute.For<ITeamService>(),
        Substitute.For<ITeamSeeding>(),
        Substitute.For<ISystemTeamSync>(),
        _users,
        Substitute.For<IAuditLogService>(),
        Substitute.For<ICampServiceRead>(),
        Substitute.For<ICampSeeding>(),
        Substitute.For<ICampRoleSeeding>(),
        Substitute.For<IConsentSubmission>(),
        Substitute.For<IMembershipCalculatorRead>(),
        Substitute.For<IHumanLifecycleService>(),
        new FakeClock(Instant.FromUtc(2026, 8, 16, 12, 0)),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new CityPlanningOptions()),
        NullLogger<DevPersonaSeeder>.Instance);
}
