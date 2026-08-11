namespace Humans.Governance.Services;

/// <summary>
/// Audit-log entity discriminators for Governance's own entities, as literals.
/// </summary>
/// <remarks>
/// The audit log stores the entity type as a string, so <c>nameof(Application)</c> would make
/// a CLR rename a silent schema change — and the entity is now <c>internal</c> to this
/// assembly, so nothing outside can name it anyway. Declaring the discriminators here is what
/// makes the rename schema-inert (memory/code/type-name-as-persisted-string.md; Store's
/// <c>Services/AuditEntityTypes.cs</c> is the shape).
/// </remarks>
internal static class AuditEntityTypes
{
    /// <summary>Matches the historical <c>nameof(Humans.Domain.Entities.Application)</c>.</summary>
    internal const string Application = "Application";
}
