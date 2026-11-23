using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Roles.Contracts;

/// <summary>Roles' cross-section reads, served whole from the cache. Callers filter and group.</summary>
public interface IRoleService : IApplicationService
{
    Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct = default);

    Task<RoleInfo?> GetRoleAsync(Guid roleId, CancellationToken ct = default);
}

/// <summary>One role with its holders.</summary>
/// <param name="Id">Never a link key: links use the slug.</param>
/// <param name="Owner">What the role belongs to.</param>
/// <param name="Name">Unique per owner, ignoring case.</param>
/// <param name="Slug">Made from the name, unique per owner.</param>
/// <param name="CustomSlug">Optional second link key.</param>
/// <param name="Description">Markdown.</param>
/// <param name="SlotCount">How many people the role wants.</param>
/// <param name="Priorities">One per slot.</param>
/// <param name="SortOrder">Order on the owner's list.</param>
/// <param name="Period">When the work happens.</param>
/// <param name="EstimatedHours">Whole hours per year; informational.</param>
/// <param name="IsPublic">False hides it from everyone but those who manage the owner's roles, and Board.</param>
/// <param name="IsCoordinator">The owner's coordinator role.</param>
/// <param name="PermissionKeys">Keys its holders hold on the owner. Empty on the coordinator role.</param>
/// <param name="Holders">Filled slots, by slot.</param>
public sealed record RoleInfo(
    Guid Id,
    Owner Owner,
    string Name,
    string Slug,
    string? CustomSlug,
    string? Description,
    int SlotCount,
    IReadOnlyList<SlotPriority> Priorities,
    int SortOrder,
    RolePeriod Period,
    int? EstimatedHours,
    bool IsPublic,
    bool IsCoordinator,
    IReadOnlyList<string> PermissionKeys,
    IReadOnlyList<RoleHolder> Holders);

/// <summary>A person in one slot.</summary>
/// <param name="UserId">The holder.</param>
/// <param name="Slot">Zero-based.</param>
public sealed record RoleHolder(Guid UserId, int Slot);
