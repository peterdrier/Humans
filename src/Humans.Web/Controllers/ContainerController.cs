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
[Route("Camp/{slug}/Containers")]
public class ContainerController : HumansControllerBase
{
    private readonly ICampService _campService;
    private readonly IContainerService _containerService;
    private readonly ICityPlanningService _cityPlanningService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<ContainerController> _logger;

    public ContainerController(
        ICampService campService,
        IContainerService containerService,
        ICityPlanningService cityPlanningService,
        IAuthorizationService authorizationService,
        UserManager<User> userManager,
        ILogger<ContainerController> logger)
        : base(userManager)
    {
        _campService = campService;
        _containerService = containerService;
        _cityPlanningService = cityPlanningService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    private sealed record CampContext(Camp Camp, bool CanManage, bool IsPrivileged);

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
        string slug, Guid userId, CancellationToken ct)
    {
        var camp = await _campService.GetCampBySlugAsync(slug, ct);
        if (camp is null) return (NotFound(), null);

        var canManage = await CanManageAsync(userId, camp.Id, ct);
        if (!canManage) return (Forbid(), null);

        var isPrivileged = await IsPrivilegedAsync(userId, ct);
        return (null, new CampContext(camp, canManage, isPrivileged));
    }

    private async Task<IActionResult?> CheckPlacementPhaseAsync(
        Guid userId, string slug, CancellationToken ct)
    {
        if (await IsPrivilegedAsync(userId, ct)) return null;
        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        if (settings.IsContainerPlacementOpen) return null;
        SetError("Container placement is currently closed.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (resolveError, ctx) = await ResolveCampContextAsync(slug, user.Id, ct);
        if (resolveError is not null) return resolveError;

        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        var containers = await _containerService.GetByCampAsync(ctx!.Camp.Id, ct);
        var sortedContainers = containers
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var placements = await _containerService.GetPlacementsByYearAsync(settings.Year, ct);

        var vm = BuildIndexViewModel(ctx, settings.Year, settings.IsContainerPlacementOpen, sortedContainers, placements);
        return View(vm);
    }

    private static ContainerIndexViewModel BuildIndexViewModel(
        CampContext ctx,
        int currentYear,
        bool isPlacementOpen,
        IReadOnlyList<ContainerDto> containers,
        IReadOnlyList<ContainerPlacementDto> placements)
    {
        var isLead = ctx.CanManage && !ctx.IsPrivileged;
        var displayName = ctx.Camp.Seasons
            .OrderByDescending(s => s.Year)
            .FirstOrDefault()?.Name
            ?? ctx.Camp.Slug;
        var containerIds = containers.Select(c => c.Id).ToHashSet();
        var placementsByContainerId = placements
            .Where(p => containerIds.Contains(p.ContainerId))
            .ToDictionary(p => p.ContainerId, ToPlacementViewModel);
        return new ContainerIndexViewModel
        {
            CampSlug = ctx.Camp.Slug,
            CampName = displayName,
            CampId = ctx.Camp.Id,
            CurrentYear = currentYear,
            CanManage = ctx.CanManage,
            IsPlacementOpen = isPlacementOpen,
            IsLeadButPhaseClosed = isLead && !isPlacementOpen,
            Containers = containers.Select(ToContainerViewModel).ToList(),
            PlacementsByContainerId = placementsByContainerId,
        };
    }

    private static ContainerPlacementViewModel ToPlacementViewModel(ContainerPlacementDto p) => new()
    {
        ContainerId = p.ContainerId,
        Year = p.Year,
        LocationGeoJson = p.LocationGeoJson,
        PlacementNotes = p.PlacementNotes,
        PlacementImageUrl = p.PlacementImageStoragePath,
        PlacementImageFileName = p.PlacementImageFileName,
    };

    private static ContainerViewModel ToContainerViewModel(ContainerDto c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        ImageUrl = c.ImageStoragePath,
        ImageFileName = c.ImageFileName,
    };

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string slug, ContainerFormModel model, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (resolveError, ctx) = await ResolveCampContextAsync(slug, user.Id, ct);
        if (resolveError is not null) return resolveError;

        var blockResult = await CheckPlacementPhaseAsync(user.Id, slug, ct);
        if (blockResult is not null) return blockResult;

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        return await TryRunContainerWriteAsync(
            () => _containerService.CreateAsync(model.ToContainerData(ctx!.Camp.Id), ct),
            slug,
            "Container added.");
    }

    [HttpPost("{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, Guid id, ContainerFormModel model, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (authError, entity) = await AuthorizeContainerOpAsync(id, ct);
        if (authError is not null) return authError;

        var blockResult = await CheckPlacementPhaseAsync(user.Id, slug, ct);
        if (blockResult is not null) return blockResult;

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        return await TryRunContainerWriteAsync(
            () => _containerService.UpdateAsync(id, model.ToContainerData(entity!.CampId), ct),
            slug,
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
    public async Task<IActionResult> Delete(string slug, Guid id, CancellationToken ct)
    {
        var entity = await GetContainerEntityAsync(id, ct);
        if (entity is null) return NotFound();

        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var authResult = await _authorizationService.AuthorizeAsync(User, entity, ContainerOperationRequirement.Manage);
        if (!authResult.Succeeded) return Forbid();

        var blockResult = await CheckPlacementPhaseAsync(user.Id, slug, ct);
        if (blockResult is not null) return blockResult;

        await _containerService.DeleteAsync(id, ct);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    private async Task<IActionResult> TryRunContainerWriteAsync(
        Func<Task> write, string slug, string successMessage)
    {
        try
        {
            await write();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Container write failed for camp {Slug}: {Message}", slug, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(Index), new { slug });
        }

        SetSuccess(successMessage);
        return RedirectToAction(nameof(Index), new { slug });
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
        };
    }
}
