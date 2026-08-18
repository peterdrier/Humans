using Microsoft.EntityFrameworkCore;

namespace Humans.Base.Data;

/// <summary>
/// Single source of truth for the per-section migrations-history table names
/// (nobodies-collective/Humans#858). The runtime registration
/// (<c>AddSectionDbContext</c>) and each design-time
/// <see cref="Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory{TContext}"/>
/// must agree exactly, or CI's from-scratch apply records a baseline in a table
/// the app never reads. Deriving both from this helper makes drift impossible.
/// </summary>
public static class SectionMigrationsHistory
{
    /// <summary>
    /// The history table for a section context: <c>AgentDbContext</c> →
    /// <c>__EFMigrationsHistory_Agent</c>.
    /// </summary>
    public static string TableFor<TContext>()
        where TContext : DbContext =>
        TableFor(typeof(TContext));

    /// <summary>
    /// Non-generic form for reflection-based callers (context types discovered
    /// at runtime, e.g. <c>PhysicalDefaultParityTests</c>).
    /// </summary>
    public static string TableFor(Type contextType) =>
        "__EFMigrationsHistory_" +
        contextType.Name.Replace("DbContext", "", StringComparison.Ordinal);
}
