using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Roles.Contracts;
using Humans.Roles.Models;
using Humans.Roles.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Roles.Controllers;

[Authorize]
[Route("Roles")]
internal sealed class RolesController(
    IRoleManagementService roles,
    IUserService userService,
    IStringLocalizer<RolesResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;

        var owners = await roles.GetOwnersAsync(ct);
        var byOwner = owners.ToDictionary(o => o.Owner);
        var slots = (await roles.GetVisibleRolesAsync(user.Id, ct))
            .Where(r => byOwner.ContainsKey(r.Owner))
            .SelectMany(r => Enumerable.Range(0, r.SlotCount)
                .Where(slot => r.Holders.All(h => h.Slot != slot))
                .Select(slot =>
                {
                    var owner = byOwner[r.Owner];
                    return new BoardSlot(OwnerLabel(owner, owners), owner.Url, r.Name, RoleUrl(owner, r), r.Period, r.Priorities[slot]);
                }))
            .OrderBy(s => RolePriorityOrder.Rank(s.Priority))
            .ThenBy(s => s.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(new BoardViewModel(slots, slots.Count(s => s.Priority == SlotPriority.Critical)));
    }

    [HttpGet("Admin")]
    [Authorize(Policy = RolesPolicies.Read)]
    public async Task<IActionResult> Admin(CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;

        var owners = await roles.GetOwnersAsync(ct);
        var all = await roles.GetRolesAsync(ct);
        // Board reads but doesn't manage, so its links go to the team's own page instead of the manage page.
        var manageable = new HashSet<Owner>();
        foreach (var o in owners.Where(o => o.IsActive))
            if (await roles.CanManageAsync(o.Owner, user.Id, ct)) manageable.Add(o.Owner);
        var rows = owners
            .Where(o => o.IsActive)
            .Select(o =>
            {
                var here = all.Where(r => r.Owner == o.Owner).ToList();
                return new AdminOwnerRow(
                    OwnerLabel(o, owners),
                    manageable.Contains(o.Owner) ? Url.Action(nameof(Owner), new { kind = o.Owner.Kind, key = o.Key })! : o.Url,
                    [.. here.Where(r => r.IsCoordinator).SelectMany(r => r.Holders).Select(h => h.UserId).Distinct()],
                    here.Count,
                    here.Sum(r => r.SlotCount - r.Holders.Count),
                    o.IsHidden);
            })
            .OrderBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(rows);
    }

    [HttpGet("{kind}/{key}")]
    public async Task<IActionResult> Owner(string kind, string key, CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        if (await FindOwnerAsync(kind, key, ct) is not { } owner) return NotFound();
        if (!await roles.CanManageAsync(owner.Owner, user.Id, ct)) return Forbid();

        return View(nameof(Owner), await OwnerPageAsync(owner, new RoleForm(), ct));
    }

    [HttpGet("{kind}/{key}/{slug}")]
    public async Task<IActionResult> Role(string kind, string key, string slug, CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        if (await FindOwnerAsync(kind, key, ct) is not { } owner) return NotFound();

        var role = (await roles.GetVisibleRolesAsync(user.Id, ct)).FirstOrDefault(r => r.Owner == owner.Owner && Matches(r, slug));
        if (role is null) return NotFound();

        return View(nameof(Role), await RolePageAsync(owner, role, user.Id, RoleForm.From(role), ct));
    }

    [HttpPost("{kind}/{key}/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string kind, string key, RoleForm form, CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        if (await FindOwnerAsync(kind, key, ct) is not { } owner) return NotFound();

        var result = await roles.CreateAsync(owner.Owner, form.ToInput(), user.Id, ct);
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            if (!await roles.CanManageAsync(owner.Owner, user.Id, ct)) return RedirectToAction(nameof(Index));
            return View(nameof(Owner), await OwnerPageAsync(owner, form, ct));
        }

        SetSuccess(localizer["Roles_Added", result.Role!.Name].Value);
        return Redirect(RoleUrl(owner, result.Role));
    }

    [HttpPost("{kind}/{key}/{slug}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string kind, string key, string slug, RoleForm form, CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        if (await FindOwnerAsync(kind, key, ct) is not { } owner) return NotFound();
        if (await FindRoleAsync(owner, slug, ct) is not { } role) return NotFound();

        var result = await roles.UpdateAsync(role.Id, form.ToInput(), user.Id, ct);
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            if (!await roles.CanManageAsync(owner.Owner, user.Id, ct)) return Redirect(RoleUrl(owner, role));
            return View(nameof(Role), await RolePageAsync(owner, role, user.Id, form, ct));
        }

        SetSuccess(localizer["Roles_Saved", result.Role!.Name].Value);
        return Redirect(RoleUrl(owner, result.Role));
    }

    [HttpPost("{kind}/{key}/{slug}/Assign")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Assign(string kind, string key, string slug, Guid userId, CancellationToken ct) =>
        ChangeAsync(kind, key, slug, ct, (role, actor) => roles.AssignAsync(role.Id, userId, actor, ct), "Roles_Assigned");

    [HttpPost("{kind}/{key}/{slug}/Unassign")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Unassign(string kind, string key, string slug, Guid userId, CancellationToken ct) =>
        ChangeAsync(kind, key, slug, ct, (role, actor) => roles.UnassignAsync(role.Id, userId, actor, ct), "Roles_Unassigned");

    [HttpPost("{kind}/{key}/{slug}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string kind, string key, string slug, CancellationToken ct)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        if (await FindOwnerAsync(kind, key, ct) is not { } owner) return NotFound();
        if (await FindRoleAsync(owner, slug, ct) is not { } role) return NotFound();

        var result = await roles.DeleteAsync(role.Id, user.Id, ct);
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            return Redirect(RoleUrl(owner, role));
        }

        SetSuccess(localizer["Roles_Deleted", role.Name].Value);
        return RedirectToAction(nameof(Owner), new { kind, key });
    }

    private async Task<IActionResult> ChangeAsync(
        string kind, string key, string slug, CancellationToken ct,
        Func<RoleInfo, Guid, Task<RoleResult>> change, string successKey)
    {
        var (error, user) = await RequireCurrentUserAsync(ct);
        if (error is not null) return error;
        if (await FindOwnerAsync(kind, key, ct) is not { } owner) return NotFound();
        if (await FindRoleAsync(owner, slug, ct) is not { } role) return NotFound();

        var result = await change(role, user.Id);
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            return Redirect(RoleUrl(owner, role));
        }

        SetSuccess(localizer[successKey, result.Role!.Name].Value);
        return Redirect(RoleUrl(owner, result.Role));
    }

    private async Task<OwnerPageViewModel> OwnerPageAsync(OwnerView owner, RoleForm addForm, CancellationToken ct)
    {
        var rows = (await roles.GetRolesAsync(ct))
            .Where(r => r.Owner == owner.Owner)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RoleRow(r, RoleUrl(owner, r)))
            .ToList();
        return new OwnerPageViewModel(owner, rows, await roles.GetPermissionChoicesAsync(owner.Owner, ct), IsRolesAdmin(), addForm);
    }

    private async Task<RolePageViewModel> RolePageAsync(OwnerView owner, RoleInfo role, Guid userId, RoleForm editForm, CancellationToken ct)
    {
        var canManage = await roles.CanManageAsync(owner.Owner, userId, ct);
        var isRolesAdmin = IsRolesAdmin();
        return new RolePageViewModel(
            owner,
            role,
            canManage,
            CanChangeHolders: role.IsCoordinator ? isRolesAdmin : canManage,
            await roles.GetContactsAsync(owner.Owner, ct),
            canManage ? await roles.GetPermissionChoicesAsync(owner.Owner, ct) : [],
            isRolesAdmin,
            editForm);
    }

    private async Task<OwnerView?> FindOwnerAsync(string kind, string key, CancellationToken ct) =>
        (await roles.GetOwnersAsync(ct)).FirstOrDefault(o =>
            string.Equals(o.Owner.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Only the owner in the route: a slug from another owner never resolves.</summary>
    private async Task<RoleInfo?> FindRoleAsync(OwnerView owner, string slug, CancellationToken ct) =>
        (await roles.GetRolesAsync(ct)).FirstOrDefault(r => r.Owner == owner.Owner && Matches(r, slug));

    private static bool Matches(RoleInfo role, string slug) =>
        string.Equals(role.Slug, slug, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role.CustomSlug, slug, StringComparison.OrdinalIgnoreCase);

    private bool IsRolesAdmin() => User.IsInRole(RoleNames.Admin) || User.IsInRole(RolesSystemRoles.RolesAdmin);

    private string RoleUrl(OwnerView owner, RoleInfo role) =>
        Url.Action(nameof(Role), new { kind = owner.Owner.Kind, key = owner.Key, slug = role.CustomSlug ?? role.Slug })!;

    /// <summary>"Parent › Child", leaving out a hidden parent so the board never names it.</summary>
    internal static string OwnerLabel(OwnerView owner, IReadOnlyList<OwnerView> owners) =>
        owner.ParentId is { } parentId
        && owners.FirstOrDefault(o => o.Owner == new Owner(owner.Owner.Kind, parentId)) is { IsHidden: false } parent
            ? $"{parent.Name} › {owner.Name}"
            : owner.Name;
}
