using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// DI extensions that wire the Infrastructure layer's persistence types
/// (<see cref="HumansDbContext"/>, <see cref="IDbContextFactory{TContext}"/>,
/// the migration runner, Identity EF stores, Data Protection key persistence).
/// Lives in Infrastructure so the Web layer never has to reference
/// <see cref="HumansDbContext"/> directly — keeping that type internal-sealed.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HumansDbContext"/> and its
    /// <see cref="IDbContextFactory{TContext}"/>, plus the
    /// <see cref="DatabaseMigrationHostedService"/> migration runner.
    /// Caller must have already registered <see cref="NpgsqlDataSource"/>
    /// and any interceptors injected into the options pipeline.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="enableDeveloperDiagnostics">
    /// When <c>true</c>, enables <c>EnableSensitiveDataLogging</c> and
    /// <c>EnableDetailedErrors</c>. Wire this from
    /// <c>builder.Environment.IsDevelopment()</c>.
    /// </param>
    public static IServiceCollection AddHumansPersistence(
        this IServiceCollection services,
        bool enableDeveloperDiagnostics)
    {
        // Configure EF Core with PostgreSQL.
        // optionsLifetime: Singleton so the Singleton IDbContextFactory<HumansDbContext> below can
        // consume DbContextOptions; HumansDbContext itself stays Scoped for normal controller/service use.
        services.AddDbContext<HumansDbContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options);
            options.AddInterceptors(sp.GetRequiredService<QueryMonitoringInterceptor>());
            // Issue #703: SaveChanges interceptor that signals UserInfo cache
            // invalidation for every persisted mutation to the 8 contributing tables.
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            // T-04: SaveChanges interceptor that wholesale-flushes the Legal-document
            // cache after any persisted write to legal_documents or document_versions.
            options.AddInterceptors(sp.GetRequiredService<LegalDocumentSaveChangesInterceptor>());
            // Suppress "First/FirstOrDefault without OrderBy" warning — the codebase universally uses
            // .FirstOrDefaultAsync(e => e.Id == id) for PK lookups which are deterministic by definition.
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
            if (enableDeveloperDiagnostics)
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        }, optionsLifetime: ServiceLifetime.Singleton);

        // Register IDbContextFactory for creating short-lived DbContext instances from the
        // Singleton Profile-section repositories (ProfileRepository, ContactFieldRepository,
        // UserEmailRepository, CommunicationPreferenceRepository). Lifetime defaults to
        // Singleton — required so Singleton consumers (the repositories) can inject it
        // without tripping scope validation.
        services.AddDbContextFactory<HumansDbContext>((sp, options) =>
        {
            ConfigureNpgsql(sp, options);
            // Issue #703: same SaveChanges interceptor for the IDbContextFactory pipeline.
            options.AddInterceptors(sp.GetRequiredService<UserInfoSaveChangesInterceptor>());
            // T-04: same Legal-cache invalidation interceptor for the IDbContextFactory pipeline.
            options.AddInterceptors(sp.GetRequiredService<LegalDocumentSaveChangesInterceptor>());
            options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
        });

        // Migration runner — IHostedLifecycleService whose StartingAsync runs
        // before any IHostedService.StartAsync, so cache-warmup / settings-
        // preload hosted services see a migrated schema.
        services.AddHostedService<DatabaseMigrationHostedService>();

        return services;
    }

    private static void ConfigureNpgsql(IServiceProvider sp, DbContextOptionsBuilder options)
    {
        options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
        {
            npgsqlOptions.UseNodaTime();
            npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
            npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        });
    }

    /// <summary>
    /// Wires ASP.NET Core Identity's EF Core stores to
    /// <see cref="HumansDbContext"/>. Typed wrapper so the Web layer never
    /// references <see cref="HumansDbContext"/> directly.
    /// </summary>
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<HumansDbContext>();

    /// <summary>
    /// Persists Data Protection keys to <see cref="HumansDbContext"/>. Typed
    /// wrapper so the Web layer never references <see cref="HumansDbContext"/>
    /// directly.
    /// </summary>
    public static IDataProtectionBuilder PersistKeysToHumansDbContext(this IDataProtectionBuilder builder) =>
        builder.PersistKeysToDbContext<HumansDbContext>();
}
