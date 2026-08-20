using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Web.Controllers;

[Route("Admin")]
public class AdminController(IUserServiceRead userService) : HumansControllerBase(userService)
{
    // AnyAdminRole so top-nav doesn't 403 for FinanceAdmin etc.; the summary strip is
    // aggregate counts safe across roles, and the tiles that aren't carry their own policy.
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AnyAdminRole)]
    public IActionResult Index() => View();
}
