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
public class CampApiController : ControllerBase
{
    private readonly ICampService _campService;

    public CampApiController(ICampService campService)
    {
        _campService = campService;
    }

    [HttpGet("{year:int}")]
    public async Task<IActionResult> GetCamps(int year)
    {
        return Ok(await _campService.GetCampPublicSummariesForYearAsync(year));
    }

    [HttpGet("{year:int}/placement")]
    public async Task<IActionResult> GetPlacement(int year)
    {
        return Ok(await _campService.GetCampPlacementSummariesForYearAsync(year));
    }
}
