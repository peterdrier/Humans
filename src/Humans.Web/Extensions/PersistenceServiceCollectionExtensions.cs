using Humans.Base.Hosting;
using Humans.Web.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Web.Extensions;

/// <summary>DI wiring for Shell's own context, the migration runner and the Identity stores.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-section contexts + factories + migration runner. Caller must
    /// register NpgsqlDataSource and any interceptors first.
    /// </summary>
    public static IServiceCollection AddHumansPersistence(this IServiceCollection services)
    {
        // Migrated by DatabaseMigrationHostedService alongside every section's context.
        services.AddSectionDbContext<SystemDbContext>(sentinelTable: "DataProtectionKeys");

        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    /// <summary>
    /// The admin diagnostics pair. Registered here rather than in Web because both types are
    /// internal to this assembly — the repository so no section can inject it, the service
    /// because a public class cannot take an internal constructor parameter. Consumers bind
    /// <see cref="Humans.Base.Interfaces.Admin.IAdminDatabaseDiagnosticsService"/>.
    /// </summary>
    public static IServiceCollection AddAdminDatabaseDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<Repositories.Admin.IAdminDatabaseDiagnosticsRepository,
            Repositories.Admin.AdminDatabaseDiagnosticsRepository>();
        services.AddScoped<Base.Interfaces.Admin.IAdminDatabaseDiagnosticsService,
            Services.AdminDatabaseDiagnosticsService>();
        return services;
    }

    /// <summary>Typed wrapper so Web never references SystemDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToSystemDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<SystemDbContext>();
}
