namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Descriptor for a per-section DbContext registered via
/// <see cref="InfrastructureServiceCollectionExtensions.AddSectionDbContext{TContext}"/>.
/// Consumed by <see cref="DatabaseMigrationHostedService"/> to migrate each section
/// context through <see cref="SectionMigrationRunner"/> after the main
/// <c>HumansDbContext</c> chain has been applied.
/// </summary>
/// <param name="ContextType">The section's DbContext CLR type.</param>
/// <param name="SentinelTable">
/// One stable table owned by the section (unqualified name, <c>public</c> schema).
/// Its presence on a database whose section history table is empty means the tables
/// were created by the historical <c>HumansDbContext</c> chain, so the section's
/// baseline migration must be recorded as applied without executing.
/// </param>
internal sealed record SectionDbContextRegistration(Type ContextType, string SentinelTable);
