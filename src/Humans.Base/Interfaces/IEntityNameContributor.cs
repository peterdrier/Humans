namespace Humans.Base.Interfaces;

/// <summary>
/// Turns bare Guids into display names. A section that owns entities other
/// pages render by id implements this and registers it from
/// <c>Section.Register</c>; a consumer injects
/// <c>IEnumerable&lt;IEntityNameContributor&gt;</c> and asks all of them.
/// </summary>
/// <remarks>
/// In Base, not a consumer's contracts leaf, because consumers must not reference
/// each other (nobodies-collective/Humans#1059).
/// </remarks>
public interface IEntityNameContributor : IFanout
{
    /// <summary>
    /// Returns an entry for each of <paramref name="ids"/> this contributor
    /// owns; ids it does not recognise are simply absent. Implementations must
    /// be read-only and must not throw on unknown ids.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EntityName>> ResolveNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <summary>
/// One resolved Guid. <paramref name="EntityType"/> is the owning section's
/// name for the thing (e.g. "User", "Team"); <paramref name="Slug"/> is its URL
/// key when it has one, so a consumer can link to it.
/// </summary>
public sealed record EntityName(string EntityType, string DisplayName, string? Slug = null);
