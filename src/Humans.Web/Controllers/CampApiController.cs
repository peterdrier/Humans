using Humans.Application.Interfaces.Camps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[AllowAnonymous]
[ApiController]
[EnableCors("BarriosPublic")]
[Route("api/barrios")]
[Route("api/camps")]
public class CampApiController(ICampService campService) : ControllerBase
{
    [HttpGet("{year:int}")]
    public async Task<IActionResult> GetCamps(int year)
    {
        return Ok(await campService.GetCampPublicSummariesForYearAsync(year));
    }

    [HttpGet("{year:int}/placement")]
    public async Task<IActionResult> GetPlacement(int year)
    {
        return Ok(await campService.GetCampPlacementSummariesForYearAsync(year));
    }
}
