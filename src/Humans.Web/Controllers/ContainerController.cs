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

    private sealed record CampContext(Camp Camp, CampSeason Season, bool CanManage, bool IsPrivileged);

    private async Task<bool> CanManageAsync(Guid userId, Guid campId, CancellationToken ct)
    {
        if (RoleChecks.IsCampAdmin(User)) return true;
        if (await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct)) return true;
        if (await _campService.IsUserCampLeadAsync(userId, campId, ct)) return true;
        return false;
    }

    private async Task<bool> IsPrivilegedAsync(Guid userId, CancellationToken ct)
    {
        if (RoleChecks.IsCampAdmin(User)) return true;
        return await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct);
    }

    private async Task<(IActionResult? Error, CampContext? Ctx)> ResolveCampContextAsync(
        string slug, int year, Guid userId, CancellationToken ct)
    {
        var camp = await _campService.GetCampBySlugAsync(slug, ct);
        if (camp is null) return (NotFound(), null);

        var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
        if (season is null) return (NotFound(), null);

        var canManage = await CanManageAsync(userId, camp.Id, ct);
        if (!canManage) return (Forbid(), null);

        var isPrivileged = await IsPrivilegedAsync(userId, ct);
        return (null, new CampContext(camp, season, canManage, isPrivileged));
    }

    private async Task<IActionResult?> CheckPlacementPhaseAsync(
        Guid userId, string slug, int year, CancellationToken ct)
    {
        if (await IsPrivilegedAsync(userId, ct)) return null;
        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        if (settings.IsContainerPlacementOpen) return null;
        SetError("Container placement is currently closed.");
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, int year, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (resolveError, ctx) = await ResolveCampContextAsync(slug, year, user.Id, ct);
        if (resolveError is not null) return resolveError;

        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        var containers = await _containerService.GetByCampAsync(ctx!.Camp.Id, year, ct);
        var sortedContainers = containers
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vm = BuildIndexViewModel(ctx, year, settings.IsContainerPlacementOpen, sortedContainers);
        return View(vm);
    }

    private static ContainerIndexViewModel BuildIndexViewModel(
        CampContext ctx, int year, bool isPlacementOpen, IReadOnlyList<ContainerDto> containers)
    {
        var isLead = ctx.CanManage && !ctx.IsPrivileged;
        return new ContainerIndexViewModel
        {
            CampSlug = ctx.Camp.Slug,
            CampName = ctx.Season.Name,
            Year = year,
            SeasonId = ctx.Season.Id,
            CampId = ctx.Camp.Id,
            CanManage = ctx.CanManage && (ctx.IsPrivileged || isPlacementOpen),
            IsPlacementOpen = isPlacementOpen,
            IsLeadButPhaseClosed = isLead && !isPlacementOpen,
            Containers = containers.Select(ToContainerViewModel).ToList()
        };
    }

    private static ContainerViewModel ToContainerViewModel(ContainerDto c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        ImageUrl = c.ImageStoragePath,
        ImageFileName = c.ImageFileName,
        IsPlaced = c.LocationGeoJson is not null,
        PlacementNotes = c.PlacementNotes,
        PlacementImageUrl = c.PlacementImageStoragePath,
        PlacementImageFileName = c.PlacementImageFileName,
    };

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string slug, int year, ContainerFormModel model, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (resolveError, ctx) = await ResolveCampContextAsync(slug, year, user.Id, ct);
        if (resolveError is not null) return resolveError;

        var blockResult = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blockResult is not null) return blockResult;

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug, year });
        }

        return await TryRunContainerWriteAsync(
            () => _containerService.CreateAsync(model.ToContainerData(ctx!.Camp.Id, year), ct),
            slug, year,
            "Container added.");
    }

    [HttpPost("{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, int year, Guid id, ContainerFormModel model, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (authError, entity) = await AuthorizeContainerOpAsync(id, ct);
        if (authError is not null) return authError;

        var blockResult = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blockResult is not null) return blockResult;

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug, year });
        }

        return await TryRunContainerWriteAsync(
            () => _containerService.UpdateAsync(id, model.ToContainerData(entity!.CampId, entity.Year), ct),
            slug, year,
            "Container updated.");
    }

    private async Task<(IActionResult? Error, Container? Entity)> AuthorizeContainerOpAsync(Guid id, CancellationToken ct)
    {
        var entity = await GetContainerEntityAsync(id, ct);
        if (entity is null) return (NotFound(), null);

        var authResult = await _authorizationService.AuthorizeAsync(User, entity, ContainerOperationRequirement.Manage);
        if (!authResult.Succeeded) return (Forbid(), null);

        return (null, entity);
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

        var blockResult = await CheckPlacementPhaseAsync(user.Id, slug, year, ct);
        if (blockResult is not null) return blockResult;

        await _containerService.DeleteAsync(id, ct);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    private async Task<IActionResult> TryRunContainerWriteAsync(
        Func<Task> write, string slug, int year, string successMessage)
    {
        try
        {
            await write();
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Index), new { slug, year });
        }

        SetSuccess(successMessage);
        return RedirectToAction(nameof(Index), new { slug, year });
    }

    private async Task<Container?> GetContainerEntityAsync(Guid id, CancellationToken ct)
    {
        var dto = await _containerService.GetByIdAsync(id, ct);
        if (dto is null) return null;

        // ContainerAuthorizationHandler only reads CampId (camp lead ownership).
        // If the handler is extended to inspect other fields, populate them here too.
        return new Container
        {
            Id = dto.Id,
            CampId = dto.CampId,
            Year = dto.Year
        };
    }
}
