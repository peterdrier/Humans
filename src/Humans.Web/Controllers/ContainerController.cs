using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
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

    private async Task<bool> AuthorizeAsync(ContainerAuthorizationTarget target, ContainerOperationRequirement requirement) =>
        (await _authorizationService.AuthorizeAsync(User, target, requirement)).Succeeded;

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, CancellationToken ct)
    {
        var camp = await _campService.GetCampBySlugAsync(slug, ct);
        if (camp is null) return NotFound();

        var target = ContainerAuthorizationTarget.ForCamp(camp.Id);
        if (!await AuthorizeAsync(target, ContainerOperationRequirement.Manage)) return Forbid();

        // Place fails for leads when phase is closed — that's the signal we
        // use to render the "lead but phase closed" message in the view.
        var canPlace = await AuthorizeAsync(target, ContainerOperationRequirement.Place);

        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        var containers = await _containerService.GetByCampAsync(camp.Id, ct);
        var sortedContainers = containers
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var placements = await _containerService.GetPlacementsByYearAsync(settings.Year, ct);

        var vm = BuildIndexViewModel(camp, settings.Year, settings.IsContainerPlacementOpen, canPlace, sortedContainers, placements);
        return View(vm);
    }

    private static ContainerIndexViewModel BuildIndexViewModel(
        CampLookup camp,
        int currentYear,
        bool isPlacementOpen,
        bool canPlace,
        IReadOnlyList<ContainerDto> containers,
        IReadOnlyList<ContainerPlacementDto> placements)
    {
        var displayName = camp.Seasons
            .OrderByDescending(s => s.Year)
            .FirstOrDefault()?.Name
            ?? camp.Slug;
        var containerIds = containers.Select(c => c.Id).ToHashSet();
        var placementsByContainerId = placements
            .Where(p => containerIds.Contains(p.ContainerId))
            .ToDictionary(p => p.ContainerId, ToPlacementViewModel);
        return new ContainerIndexViewModel
        {
            CampSlug = camp.Slug,
            CampName = displayName,
            CampId = camp.Id,
            CurrentYear = currentYear,
            CanManage = true, // controller already authorized Manage above
            IsPlacementOpen = isPlacementOpen,
            IsLeadButPhaseClosed = !canPlace && !isPlacementOpen,
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

        var camp = await _campService.GetCampBySlugAsync(slug, ct);
        if (camp is null) return NotFound();

        // Place includes the phase-gate for leads — write actions need it.
        var target = ContainerAuthorizationTarget.ForCamp(camp.Id);
        if (!await AuthorizeAsync(target, ContainerOperationRequirement.Manage)) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        return await TryRunContainerWriteAsync(
            () => _containerService.CreateAsync(model.ToContainerData(camp.Id), user.Id, ct),
            slug,
            "Container added.");
    }

    [HttpPost("{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, Guid id, ContainerFormModel model, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (notFound, container) = await ResolveAndAuthorizeAsync(id, ContainerOperationRequirement.Manage, ct);
        if (notFound is not null) return notFound;

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        return await TryRunContainerWriteAsync(
            () => _containerService.UpdateAsync(id, model.ToContainerData(container!.CampId), user.Id, ct),
            slug,
            "Container updated.");
    }

    [HttpPost("{id}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string slug, Guid id, CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return userError;

        var (notFound, _) = await ResolveAndAuthorizeAsync(id, ContainerOperationRequirement.Manage, ct);
        if (notFound is not null) return notFound;

        await _containerService.DeleteAsync(id, user.Id, ct);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    private async Task<(IActionResult? Error, ContainerDto? Container)> ResolveAndAuthorizeAsync(
        Guid id, ContainerOperationRequirement requirement, CancellationToken ct)
    {
        var dto = await _containerService.GetByIdAsync(id, ct);
        if (dto is null) return (NotFound(), null);

        if (!await AuthorizeAsync(ContainerAuthorizationTarget.For(dto), requirement))
        {
            return (Forbid(), null);
        }
        return (null, dto);
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
}
