namespace Humans.Base.Interfaces;

/// <summary>
/// Turns bare Guids into display names. A section that owns entities other
/// pages render by id implements this and registers it from
/// <c>Section.Register</c>; a consumer injects
/// <c>IEnumerable&lt;IEntityNameContributor&gt;</c> and asks all of them.
/// </summary>
/// <remarks>
/// <para>
/// Peter's ruling on nobodies-collective/Humans#1059: a system-wide
/// Guid-resolution fan-out, so anything holding bare Guids — AuditLog, Search —
/// gets names without linking to the sections that own them. The arrow inverts:
/// sections reference this contract, and the consumer references no section.
/// </para>
/// <para>
/// It lives in Base rather than in one consumer's section because two consumers
/// would otherwise have to reference each other. Base names no section type, so
/// the graph stays acyclic.
/// </para>
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
