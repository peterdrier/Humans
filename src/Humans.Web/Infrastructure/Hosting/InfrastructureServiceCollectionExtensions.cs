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
/// reference Web. The rest of the type came here at lane 5b-6, which deleted
/// Humans.Infrastructure; the two architecture rules that read
/// <c>typeof(InfrastructureServiceCollectionExtensions).Assembly</c> as "the Humans.Infrastructure
/// assembly" were self-retiring on an empty <c>Repositories/</c> and went with it. The namespace
/// is kept so no call site moved, and it is what keeps this type distinct from
/// <c>Humans.Web.Extensions.InfrastructureServiceCollectionExtensions</c>, the roll-call.
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

    /// <summary>
    /// The admin diagnostics pair. Registered here rather than in Web because both types are
    /// internal to this assembly — the repository so no section can inject it, the service
    /// because a public class cannot take an internal constructor parameter. Consumers bind
    /// <see cref="Application.Interfaces.Admin.IAdminDatabaseDiagnosticsService"/>.
    /// </summary>
    public static IServiceCollection AddAdminDatabaseDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<Repositories.Admin.IAdminDatabaseDiagnosticsRepository,
            Repositories.Admin.AdminDatabaseDiagnosticsRepository>();
        services.AddScoped<Application.Interfaces.Admin.IAdminDatabaseDiagnosticsService,
            Services.AdminDatabaseDiagnosticsService>();
        return services;
    }

    /// <summary>Typed wrapper so Web never references SystemDbContext directly.</summary>
    public static IDataProtectionBuilder PersistKeysToSystemDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<SystemDbContext>();
}
