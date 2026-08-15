using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// DI wiring for Base's own context, the migration runner and the Identity stores.
/// </summary>
/// <remarks>
/// The generic <c>AddSectionDbContext&lt;TContext&gt;</c> seam left for
/// <see cref="SectionDbContextServiceCollectionExtensions"/> in the Humans.Interfaces (Base)
/// assembly at G5 lane 3a-2 (nobodies-collective/Humans#866) — sections call it and may not
/// reference this project. This type deliberately stayed put: two architecture rules read
/// <c>typeof(InfrastructureServiceCollectionExtensions).Assembly</c> as "the Humans.Infrastructure
/// assembly" (<c>IRepositoryImplementationsAreSealedRule</c>,
/// <c>RepositoryImplementationsLiveInInfrastructureRule</c>) and would have retargeted onto Base
/// and passed vacuously. The namespace is shared, so the call below binds to the moved method
/// with no <c>using</c> change.
/// </remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-section contexts + factories + migration runner. Caller must
    /// register NpgsqlDataSource and any interceptors first.
    /// </summary>
    public static IServiceCollection AddHumansPersistence(this IServiceCollection services)
    {
        // Base's own context (nobodies-collective/Humans#858), migrated by
        // DatabaseMigrationHostedService alongside every section's. UsersDbContext used to be
        // registered here too — it carries the Identity tables and was the last context left
        // in Base — and it moved into Humans.Users' Section.Register with the section
        // (#866, G5 lane 2, design §15 step 11).
        services.AddSectionDbContext<SystemDbContext>(sentinelTable: "DataProtectionKeys");

        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    /// <summary>Typed wrapper so Web never references SystemDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToSystemDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<SystemDbContext>();
}
