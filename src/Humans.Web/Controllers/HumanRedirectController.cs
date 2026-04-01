using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Temporary redirects from old /Human/* routes to new /Profile/* routes.
/// This controller exists to prevent broken bookmarks and external links.
/// Clean up after 2026-05-01 — by then all cached links and bookmarks should have updated.
/// </summary>
[Obsolete("Temporary redirect shim. Remove after 2026-05-01.")]
[Authorize]
[Route("Human")]
public class HumanRedirectController : Controller
{
    [HttpGet("Admin")]
    public IActionResult AdminList() =>
        RedirectToActionPermanent(nameof(ProfileController.AdminList), "Profile");

    [HttpGet("{id:guid}")]
    public IActionResult ViewProfile(Guid id) =>
        RedirectToActionPermanent(nameof(ProfileController.ViewProfile), "Profile", new { id });

    [HttpGet("{id:guid}/Popover")]
    public IActionResult Popover(Guid id) =>
        RedirectToActionPermanent(nameof(ProfileController.Popover), "Profile", new { id });

    [HttpGet("{id:guid}/SendMessage")]
    public IActionResult SendMessage(Guid id) =>
        RedirectToActionPermanent(nameof(ProfileController.SendMessage), "Profile", new { id });

    [HttpGet("{id:guid}/Admin")]
    public IActionResult AdminDetail(Guid id) =>
        RedirectToActionPermanent(nameof(ProfileController.AdminDetail), "Profile", new { id });

    [HttpGet("{id:guid}/Outbox")]
    public IActionResult Outbox(Guid id) =>
        RedirectToActionPermanent(nameof(ProfileController.AdminOutbox), "Profile", new { id });

    [HttpGet("{id:guid}/Admin/Roles/Add")]
    public IActionResult AddRole(Guid id) =>
        RedirectToActionPermanent(nameof(ProfileController.AddRole), "Profile", new { id });

    [HttpGet("Admin/Contacts")]
    public IActionResult Contacts(string? search) =>
        RedirectToActionPermanent(nameof(ContactsController.Index), "Contacts", new { search });

    [HttpGet("Admin/Contacts/{id:guid}")]
    public IActionResult ContactDetail(Guid id) =>
        RedirectToActionPermanent(nameof(ContactsController.Detail), "Contacts", new { id });

    [HttpGet("Admin/Contacts/Create")]
    public IActionResult CreateContact() =>
        RedirectToActionPermanent(nameof(ContactsController.Create), "Contacts");
}

/// <summary>
/// Temporary redirect from old /api/humans/* routes to new /api/profiles/* routes.
/// </summary>
[Authorize]
[ApiController]
[Route("api/humans")]
public class HumanApiRedirectController : ControllerBase
{
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q) =>
        RedirectToActionPermanent(nameof(ProfileApiController.Search), "ProfileApi", new { q });
}
