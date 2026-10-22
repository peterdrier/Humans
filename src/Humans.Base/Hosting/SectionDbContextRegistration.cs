namespace Humans.Base.Hosting;

/// <summary>
/// A per-section DbContext registered via
/// <see cref="SectionDbContextServiceCollectionExtensions.AddSectionDbContext{TContext}"/>, which
/// <see cref="DatabaseMigrationHostedService"/> migrates in registration order.
/// </summary>
/// <param name="ContextType">The section's DbContext CLR type.</param>
/// <param name="SentinelTable">
/// One stable table the section owns (unqualified, <c>public</c> schema). Present while the section's history table is
/// empty, it means the deleted root <c>HumansDbContext</c> chain created the tables, so the baseline is recorded as
/// applied without executing.
/// </param>
internal sealed record SectionDbContextRegistration(Type ContextType, string SentinelTable);
