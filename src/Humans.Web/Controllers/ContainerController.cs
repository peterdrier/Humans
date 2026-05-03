using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Camp/{slug}/Season/{year}/Containers")]
public class ContainerController : HumansControllerBase
{
    private readonly ICampService _campService;
    private readonly IContainerService _containerService;
    private readonly ICityPlanningService _cityPlanningService;
    private readonly IAuthorizationService _authorizationService;

    public ContainerController(
        ICampService campService,
        IContainerService containerService,
        ICityPlanningService cityPlanningService,
        IAuthorizationService authorizationService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _campService = campService;
        _containerService = containerService;
        _cityPlanningService = cityPlanningService;
        _authorizationService = authorizationService;
    }

    private async Task<bool> CanManageAsync(Guid userId, Camp camp, CancellationToken ct)
    {
        if (RoleChecks.IsCampAdmin(User)) return true;
        if (await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct)) return true;
        if (await _campService.IsUserCampLeadAsync(userId, camp.Id, ct)) return true;
        return false;
    }

    private async Task<bool> IsPrivilegedAsync(Guid userId, CancellationToken ct)
    {
        if (RoleChecks.IsCampAdmin(User)) return true;
        return await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct);
    }

    private async Task<(bool blocked, IActionResult? result)> CheckPlacementPhaseAsync(
        Guid userId, string slug, int year, CancellationToken ct)
    {
        if (await IsPrivilegedAsync(userId, ct)) return (false, null);
        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        if (settings.IsContainerPlacementOpen) return (false, null);
        SetError("Container placement is currently closed.");
        return (true, RedirectToAction(nameof(Index), new { slug, year }));
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, int year, CancellationToken ct)
    {
        var camp = await _campService.GetCampBySlugAsync(slug, ct);
        if (camp is null) return NotFound();

        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (season is null) return NotFound();

        var canManage = await CanManageAsync(user.Id, camp, ct);
        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        var isPrivileged = await IsPrivilegedAsync(user.Id, ct);

        var containers = await _containerService.GetBySeasonAsync(season.Id, ct);

        var isLead = canManage && !isPrivileged;

        var vm = new ContainerIndexViewModel
        {
            CampSlug = camp.Slug,
            CampName = season.Name,
            Year = year,
            SeasonId = season.Id,
            CanManage = canManage && (isPrivileged || settings.IsContainerPlacementOpen),
            IsPlacementOpen = settings.IsContainerPlacementOpen,
            IsLeadButPhaseClosed = isLead && !settings.IsContainerPlacementOpen,
            Containers = containers.Select(c => new ContainerViewModel
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                ImageUrl = c.ImageStoragePath,
                ImageFileName = c.ImageFileName,
                IsPlaced = c.LocationGeoJson is not null,
            }).ToList()
        };

        return View(vm);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string slug, int year, ContainerFormModel model, CancellationToken ct)
    {
        var camp = await _campService.GetCampBySlugAsync(slug, ct);
        if (camp is null) return NotFound();

        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        if (!await CanManageAsync(user.Id, camp, ct)) return Forbid();

        var (blocked, blockResult) = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blocked) return blockResult!;

        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (season is null) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug, year });
        }

        await _containerService.CreateAsync(new ContainerData(
            CampSeasonId: season.Id,
            Year: year,
            Name: model.Name,
            Description: model.Description), ct);

        SetSuccess("Container added.");
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    [HttpPost("{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, int year, Guid id, ContainerFormModel model, CancellationToken ct)
    {
        var entity = await GetContainerEntityAsync(id, ct);
        if (entity is null) return NotFound();

        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var authResult = await _authorizationService.AuthorizeAsync(User, entity, ContainerOperationRequirement.Manage);
        if (!authResult.Succeeded) return Forbid();

        var (blocked, blockResult) = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blocked) return blockResult!;

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug, year });
        }

        await _containerService.UpdateAsync(id, new ContainerData(
            CampSeasonId: entity.CampSeasonId,
            Year: entity.Year,
            Name: model.Name,
            Description: model.Description), ct);

        SetSuccess("Container updated.");
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    [HttpPost("{id}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string slug, int year, Guid id, CancellationToken ct)
    {
        var entity = await GetContainerEntityAsync(id, ct);
        if (entity is null) return NotFound();

        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var authResult = await _authorizationService.AuthorizeAsync(User, entity, ContainerOperationRequirement.Manage);
        if (!authResult.Succeeded) return Forbid();

        var (blocked, blockResult) = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blocked) return blockResult!;

        await _containerService.DeleteAsync(id, ct);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    [HttpPost("{id}/Image/Upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadImage(string slug, int year, Guid id, IFormFile? file, CancellationToken ct)
    {
        var entity = await GetContainerEntityAsync(id, ct);
        if (entity is null) return NotFound();

        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var authResult = await _authorizationService.AuthorizeAsync(User, entity, ContainerOperationRequirement.Manage);
        if (!authResult.Succeeded) return Forbid();

        var (blocked, blockResult) = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blocked) return blockResult!;

        if (file is null || file.Length == 0)
        {
            SetError("Please select a file to upload.");
            return RedirectToAction(nameof(Index), new { slug, year });
        }

        try
        {
            await _containerService.UploadImageAsync(id, file.OpenReadStream(), file.FileName, file.ContentType, file.Length, ct);
            SetSuccess("Image uploaded.");
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index), new { slug, year });
    }

    [HttpPost("{id}/Image/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteImage(string slug, int year, Guid id, CancellationToken ct)
    {
        var entity = await GetContainerEntityAsync(id, ct);
        if (entity is null) return NotFound();

        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var authResult = await _authorizationService.AuthorizeAsync(User, entity, ContainerOperationRequirement.Manage);
        if (!authResult.Succeeded) return Forbid();

        var (blocked, blockResult) = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blocked) return blockResult!;

        await _containerService.DeleteImageAsync(id, ct);
        SetSuccess("Image removed.");
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    private async Task<Container?> GetContainerEntityAsync(Guid id, CancellationToken ct)
    {
        var dto = await _containerService.GetByIdAsync(id, ct);
        if (dto is null) return null;

        // ContainerAuthorizationHandler only reads CampSeasonId (to check camp lead ownership).
        // If the handler is extended to inspect other fields, populate them here too.
        return new Container
        {
            Id = dto.Id,
            CampSeasonId = dto.CampSeasonId,
            Year = dto.Year
        };
    }
}
