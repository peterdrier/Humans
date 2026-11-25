using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Permissions.Contracts;
using Humans.Roles.Contracts;
using Humans.Roles.Services;
using Humans.Teams.Contracts;

namespace Humans.Roles.Models;

/// <summary>One open slot on the board.</summary>
internal sealed record BoardSlot(string Owner, string OwnerUrl, string Role, string RoleUrl, RolePeriod Period, SlotPriority Priority);

internal sealed record BoardViewModel(IReadOnlyList<BoardSlot> Slots, int CriticalCount);

/// <summary>One owner on the admin list.</summary>
internal sealed record AdminOwnerRow(string Owner, string Url, IReadOnlyList<Guid> Coordinators, int RoleCount, int OpenSlots, bool IsHidden);

/// <summary>A role on an owner's list or card.</summary>
internal sealed record RoleRow(RoleInfo Role, string Url)
{
    public int OpenCount => Role.SlotCount - Role.Holders.Count;

    /// <summary>The most pressing priority among the open slots, or null when every slot is filled.</summary>
    public SlotPriority? TopOpenPriority => Enumerable.Range(0, Role.SlotCount)
        .Where(slot => Role.Holders.All(h => h.Slot != slot))
        .Select(slot => (SlotPriority?)Role.Priorities[slot])
        .MinBy(p => RolePriorityOrder.Rank(p!.Value));
}

internal sealed record OwnerPageViewModel(
    OwnerView Owner,
    IReadOnlyList<RoleRow> Roles,
    IReadOnlyList<Permission> Permissions,
    bool CanSetCoordinator,
    RoleForm AddForm);

internal sealed record RolePageViewModel(
    OwnerView Owner,
    RoleInfo Role,
    bool CanManage,
    bool CanChangeHolders,
    IReadOnlyList<Guid> Contacts,
    IReadOnlyList<Permission> Permissions,
    bool CanSetCoordinator,
    RoleForm EditForm);

internal sealed record RolesCardViewModel(IReadOnlyList<RoleRow> Roles, bool CanManage, string ManageUrl);

/// <summary>What the add and edit forms post.</summary>
internal sealed class RoleForm
{
    public string Name { get; set; } = string.Empty;
    public string? CustomSlug { get; set; }
    public string? Description { get; set; }
    [BindRequired]
    public int SlotCount { get; set; } = 1;
    public List<SlotPriority> Priorities { get; set; } = [];
    [BindRequired]
    public int SortOrder { get; set; }
    [BindRequired]
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;
    public int? EstimatedHours { get; set; }
    [BindRequired]
    public bool IsPublic { get; set; } = true;
    [BindRequired]
    public bool IsCoordinator { get; set; }
    public List<string> PermissionKeys { get; set; } = [];

    public static RoleForm From(RoleInfo role) => new()
    {
        Name = role.Name,
        CustomSlug = role.CustomSlug,
        Description = role.Description,
        SlotCount = role.SlotCount,
        Priorities = [.. role.Priorities],
        SortOrder = role.SortOrder,
        Period = role.Period,
        EstimatedHours = role.EstimatedHours,
        IsPublic = role.IsPublic,
        IsCoordinator = role.IsCoordinator,
        PermissionKeys = [.. role.PermissionKeys],
    };

    /// <summary>New slots start at no priority; extra priorities beyond the slot count are dropped.</summary>
    public RoleInput ToInput() => new(
        Name,
        CustomSlug,
        Description,
        SlotCount,
        [.. Enumerable.Range(0, Math.Max(SlotCount, 0)).Select(i => i < Priorities.Count ? Priorities[i] : SlotPriority.None)],
        SortOrder,
        Period,
        EstimatedHours,
        IsPublic,
        IsCoordinator,
        PermissionKeys);
}

internal static class RolePriorityOrder
{
    /// <summary>Critical first, unset last.</summary>
    public static int Rank(SlotPriority priority) => priority switch
    {
        SlotPriority.Critical => 0,
        SlotPriority.Important => 1,
        SlotPriority.NiceToHave => 2,
        _ => 3,
    };
}

/// <summary>The add or edit form, filled from <paramref name="Form"/> so a refused post keeps what was typed.</summary>
internal sealed record RoleFormViewModel(
    RoleForm Form,
    string Id,
    bool IsNew,
    string Action,
    IReadOnlyList<Permission> Permissions,
    bool CanSetCoordinator);
