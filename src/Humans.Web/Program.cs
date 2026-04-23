using System.Diagnostics;
using System.IO.Compression;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Humans.Web.Authorization;
using Humans.Web.Health;
using Humans.Web.Hubs;
using Humans.Web.Middleware;
using Microsoft.Extensions.Localization;
using Npgsql;
using Humans.Infrastructure.Logging;
using Serilog;
using Serilog.Events;
using Humans.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Humans.Web")
    .Enrich.With<PiiRedactionEnricher>()
    .WriteTo.Console()
    .WriteTo.Sink(InMemoryLogSink.Instance, LogEventLevel.Warning);

if (Debugger.IsAttached)
{
    var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "human");
    Directory.CreateDirectory(logDir);
    logConfig.WriteTo.File(
        Path.Combine(logDir, "humans-.log"),
        rollingInterval: RollingInterval.Day);
}

Log.Logger = logConfig.CreateLogger();

builder.Host.UseSerilog();

// Validate the DI container at startup so obvious cycles and captive
// dependencies fail fast instead of surfacing on first request. Factory-lambda
// registrations still need explicit smoke coverage because the container can't
// inspect arbitrary factory bodies until resolve time.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Add services to the container

// Configuration registry — auto-collects metadata about every config setting the app touches.
// Created as a concrete instance so it can be used during startup config (before DI is built).
var configRegistry = new ConfigurationRegistry();
builder.Services.AddSingleton(configRegistry);

// Configure NodaTime clock
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
if (!builder.Environment.IsProduction())
{
    builder.Services.AddScoped<DevelopmentBudgetSeeder>();
    builder.Services.AddScoped<DevelopmentDashboardSeeder>();
}

// Configure JSON options with NodaTime support
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
});

// Register connection string in the config registry for the Admin Configuration page
builder.Configuration.GetRequiredSetting(
    configRegistry, "ConnectionStrings:DefaultConnection", "Database", isSensitive: true);

// Configure Npgsql data source with NodaTime and dynamic JSON (for jsonb Dictionary columns).
// Registered as a DI singleton so the connection string is resolved at service-resolution time,
// allowing integration tests to override configuration via WebApplicationFactory.
builder.Services.AddSingleton(sp =>
{
    var connStr = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    var dsb = new NpgsqlDataSourceBuilder(connStr);
    dsb.UseNodaTime();
    dsb.EnableDynamicJson();
    return dsb.Build();
});

// Query monitoring — singleton interceptor tracks execution counts by table + operation
builder.Services.AddSingleton<QueryStatistics>();
builder.Services.AddSingleton<QueryMonitoringInterceptor>();

// Cache monitoring — decorator wraps real MemoryCache to track hit/miss stats per key type.
// Register TrackingMemoryCache as both IMemoryCache (decorator) and ICacheStatsProvider (stats).
builder.Services.AddSingleton<TrackingMemoryCache>(sp =>
    new TrackingMemoryCache(new MemoryCache(new MemoryCacheOptions())));
builder.Services.AddSingleton<IMemoryCache>(sp => sp.GetRequiredService<TrackingMemoryCache>());
builder.Services.AddSingleton<ICacheStatsProvider>(sp => sp.GetRequiredService<TrackingMemoryCache>());

// Configure EF Core with PostgreSQL.
// optionsLifetime: Singleton so the Singleton IDbContextFactory<HumansDbContext> below can
// consume DbContextOptions; HumansDbContext itself stays Scoped for normal controller/service use.
builder.Services.AddDbContext<HumansDbContext>((sp, options) =>
{
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
    {
        npgsqlOptions.UseNodaTime();
        npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
        npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    });
    options.AddInterceptors(sp.GetRequiredService<QueryMonitoringInterceptor>());
    // Suppress "First/FirstOrDefault without OrderBy" warning — the codebase universally uses
    // .FirstOrDefaultAsync(e => e.Id == id) for PK lookups which are deterministic by definition.
    options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
    if (builder.Environment.IsDevelopment())
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
builder.Services.AddDbContextFactory<HumansDbContext>((sp, options) =>
{
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsqlOptions =>
    {
        npgsqlOptions.UseNodaTime();
        npgsqlOptions.MigrationsAssembly("Humans.Infrastructure");
        npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    });
    options.ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning));
});

// Persist Data Protection keys to the database so auth cookies survive container restarts
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<HumansDbContext>()
    .SetApplicationName("Humans.Web");

// Configure ASP.NET Core Identity
builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<HumansDbContext>()
    .AddDefaultTokenProviders();

// Magic link tokens use DataProtection with explicit 15-minute lifetime (not Identity token providers).

// Configure cookie security policy (TLS terminated by Coolify/reverse proxy)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Configure Authentication with Google OAuth
builder.Services.AddAuthentication()
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration.GetRequiredSetting(
                configRegistry, "Authentication:Google:ClientId", "Authentication", isSensitive: true)
            ?? throw new InvalidOperationException("Google ClientId not configured.");
        options.ClientSecret = builder.Configuration.GetRequiredSetting(
                configRegistry, "Authentication:Google:ClientSecret", "Authentication", isSensitive: true)
            ?? throw new InvalidOperationException("Google ClientSecret not configured.");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.SaveTokens = false;
        options.Events = new Microsoft.AspNetCore.Authentication.OAuth.OAuthEvents
        {
            OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GoogleOAuth");

                var failureMessage = context.Failure?.Message ?? string.Empty;
                var isCorrelationFailure = failureMessage.Contains("Correlation", StringComparison.OrdinalIgnoreCase);
                var isAccessDenied = failureMessage.Contains("access_denied", StringComparison.OrdinalIgnoreCase)
                    || failureMessage.Contains("denied by the resource owner", StringComparison.OrdinalIgnoreCase);

                var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // All three categories are expected user behavior, but support needs to trace them
                // when someone calls in. Log at Warning with the client IP so /Admin/Logs surfaces
                // them and the event can be correlated to a user report. Stack traces dropped —
                // the failure reason and IP are the actionable bits. See #483.
                if (isAccessDenied)
                {
                    logger.LogWarning(
                        "Google sign-in cancelled by user (access_denied) from {ClientIp}", clientIp);
                }
                else if (isCorrelationFailure)
                {
                    logger.LogWarning(
                        "Google sign-in correlation cookie missing from {ClientIp} (stale or duplicate request)", clientIp);
                }
                else
                {
                    logger.LogWarning(
                        context.Failure, "Google sign-in failed from {ClientIp}: {Error}", clientIp, failureMessage);
                }

                context.Response.Redirect("/Account/Login?error=sign-in-failed");
                context.HandleResponse();
                return Task.CompletedTask;
            }
        };
    });

// Configure Authorization — registers all canonical policies (see docs/authorization-inventory.md)
builder.Services.AddHumansAuthorizationPolicies();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleAssignmentClaimsTransformation>();

// Configure Hangfire
builder.Services.AddHangfire((sp, config) =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();

    // Skip Postgres storage in test environment — Hangfire's static GlobalConfiguration
    // and JobStorage.Current are per-AppDomain, which conflicts with parallel
    // WebApplicationFactory instances each pointing at different Testcontainers.
    if (!sp.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing"))
    {
        config.UsePostgreSqlStorage(options =>
            options.UseNpgsqlConnection(
                sp.GetRequiredService<IConfiguration>()
                    .GetConnectionString("DefaultConnection")!),
            new PostgreSqlStorageOptions
            {
                DistributedLockTimeout = TimeSpan.FromSeconds(5)
            });
    }
});

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfireServer();
}

// Configure OpenTelemetry
var serviceName = "Humans.Web";
var serviceVersion = "1.0.0";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .WithTracing(tracing => tracing
        .AddSource(serviceName)
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(
                builder.Configuration.GetOptionalSetting(
                    configRegistry, "OpenTelemetry:OtlpEndpoint", "OpenTelemetry")
                ?? "http://localhost:4317");
        }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("Humans.Metrics")
        .AddMeter("Npgsql")
        .AddPrometheusExporter());

// Register activity source for custom tracing
builder.Services.AddSingleton(new ActivitySource(serviceName, serviceVersion));

// Configure Health Checks
builder.Services.AddHealthChecks()
    .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgresql")
    .AddHangfire(options => options.MinimumAvailableServers = 1, name: "hangfire")
    .AddCheck<ConfigurationHealthCheck>("configuration")
    .AddCheck<SmtpHealthCheck>("smtp")
    .AddCheck<GitHubHealthCheck>("github")
    .AddCheck<GoogleWorkspaceHealthCheck>("google-workspace");

builder.Services.AddHumansInfrastructure(builder.Configuration, builder.Environment, configRegistry);

// Configure Response Compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// Configure Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Exclude error pages — prevent rate limit cascade when error page triggers additional requests
        if (context.Request.Path.StartsWithSegments("/Home/Error", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter(string.Empty);

        // Exclude favicon — browsers request this automatically on every page load
        if (context.Request.Path == "/favicon.ico")
            return RateLimitPartition.GetNoLimiter(string.Empty);

        // Exclude profile picture requests — list pages legitimately load ~30 images at once
        if (context.Request.Path.StartsWithSegments("/Profile/Picture", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter(string.Empty);

        // Exclude SignalR hubs — long-polling fallback sends one POST per invoke,
        // which trivially exceeds the global 100/min cap during active use.
        // SignalR manages its own backpressure; abuse is handled via auth on the hub.
        if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter(string.Empty);

        // Exclude local network — e2e tests and internal tooling run from 192.168.*
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (remoteIp is not null && remoteIp.StartsWith("192.168.", StringComparison.Ordinal))
            return RateLimitPartition.GetNoLimiter(string.Empty);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RateLimiting");
        var remoteIp = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        var authenticatedUser = context.HttpContext.User.Identity?.IsAuthenticated == true
            ? context.HttpContext.User.Identity.Name
            : null;
        var identity = authenticatedUser ?? remoteIp ?? "anonymous";

        // Best-effort reverse DNS lookup
        string? reverseDns = null;
        if (remoteIp is not null)
        {
            try
            {
                using var dnsCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(dnsCts.Token, cancellationToken);
                var hostEntry = await System.Net.Dns.GetHostEntryAsync(remoteIp, linkedCts.Token);
                reverseDns = hostEntry.HostName;
            }
            catch
            {
                // DNS lookup failed or timed out — continue without it
            }
        }

        // Permit usage info from the lease metadata
        string? permitInfo = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            permitInfo = $"RetryAfter={retryAfter.TotalSeconds:F0}s";
        }

        logger.LogWarning(
            "Rate limit exceeded for {Identity} (IP={RemoteIp}, ReverseDns={ReverseDns}, User={AuthenticatedUser}, {PermitInfo}): {Method} {Path}",
            identity, remoteIp ?? "unknown", reverseDns ?? "N/A",
            authenticatedUser ?? "anonymous", permitInfo ?? "no metadata",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path);
    };
});

// Forwarded headers (X-Forwarded-For, X-Forwarded-Proto) are enabled via
// ASPNETCORE_FORWARDEDHEADERS_ENABLED=true in the deployment environment.
// No explicit config needed — the app is only reachable through Traefik/Coolify
// on internal Docker networks, so trusting any proxy is safe.

// Session (used for browser-detected timezone — no DB migration needed)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Configure Localization
builder.Services.AddLocalization();

// Add Controllers with Views
builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add<MembershipRequiredFilter>();
        options.Filters.Add<Humans.Web.Filters.AuthorizationPillFilter>();
        options.Filters.Add<Humans.Web.Filters.GlobalExceptionFilter>();
    })
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

var supportedCultures = CultureCatalog.SupportedCultureCodes.ToArray();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("en");
    options.AddSupportedCultures(supportedCultures);
    options.AddSupportedUICultures(supportedCultures);
    options.AddInitialRequestCultureProvider(new CustomRequestCultureProvider(async context =>
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userManager = context.RequestServices.GetRequiredService<UserManager<User>>();
            var user = await userManager.GetUserAsync(context.User);
            if (user is not null && !string.IsNullOrEmpty(user.PreferredLanguage))
            {
                return new ProviderCultureResult(culture: "en", uiCulture: user.PreferredLanguage);
            }
        }
        return null;
    }));
});

var app = builder.Build();

// Initialize timezone-aware display extensions with IHttpContextAccessor
// so all Instant.ToDisplay*() calls automatically use the user's session timezone.
DateTimeDisplayExtensions.Initialize(app.Services.GetRequiredService<IHttpContextAccessor>());

// Eagerly resolve IHumansMetrics so the background gauge-refresh timer starts
// immediately — otherwise observable gauges emit nothing until first injection.
app.Services.GetRequiredService<IHumansMetrics>();

// Localization diagnostic check
{
    using var scope = app.Services.CreateScope();
    var localizerFactory = scope.ServiceProvider.GetRequiredService<IStringLocalizerFactory>();
    var localizer = localizerFactory.Create(typeof(Humans.Web.SharedResource));
    var testKey = "Dashboard_Welcome";
    var result = localizer[testKey];

    if (result.ResourceNotFound)
    {
        Log.Error("LOCALIZATION BROKEN: Resource key '{Key}' not found. SearchedLocation: {Location}",
            testKey, result.SearchedLocation);
        Log.Error("SharedResource type: {TypeName}, Assembly: {Assembly}",
            typeof(Humans.Web.SharedResource).FullName, typeof(Humans.Web.SharedResource).Assembly.GetName().Name);

        // List embedded resources for debugging
        var assembly = typeof(Humans.Web.SharedResource).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        Log.Error("Embedded resources in {Assembly}: {Resources}",
            assembly.GetName().Name, string.Join(", ", resourceNames));

        // Check satellite assemblies
        foreach (var culture in new[] { "en", "es", "de", "it", "fr", "ca" })
        {
            try
            {
                var satAssembly = assembly.GetSatelliteAssembly(new System.Globalization.CultureInfo(culture));
                var satResources = satAssembly.GetManifestResourceNames();
                Log.Information("Satellite assembly [{Culture}] resources: {Resources}",
                    culture, string.Join(", ", satResources));
            }
            catch (Exception ex)
            {
                Log.Warning("No satellite assembly for culture '{Culture}': {Error}", culture, ex.Message);
            }
        }
    }
    else
    {
        Log.Information("Localization OK: '{Key}' => '{Value}'", testKey, result.Value);
    }
}

// Configure the HTTP request pipeline

// Forwarded headers must be first (for reverse proxy)
app.UseForwardedHeaders();

// Global catch-all logger — logs every unhandled exception regardless of what
// downstream middleware does. Must be first after forwarded headers.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        throw;
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStatusCodePagesWithReExecute("/Home/Error/{0}");

if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

app.UseStaticFiles();

// Serve .well-known directory (blocked by default since it starts with a dot)
if (app.Environment.IsDevelopment())
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(
            Path.Combine(app.Environment.WebRootPath, ".well-known")),
        RequestPath = "/.well-known",
        ServeUnknownFileTypes = true
    });
}

// HTTP Security Headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
    await next();
});

app.UseMiddleware<CspNonceMiddleware>();

app.UseRouting();

// Rate limiting
app.UseRateLimiter();

// Serilog request logging
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

app.UseSession();

app.UseRequestLocalization();

// Health check endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = WriteDetailedHealthResponse
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // Liveness check - just confirms the app is running
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = _ => true // Readiness check - confirms all dependencies are available
});

// Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint("/metrics");

// Version endpoint (unauthenticated)
app.MapGet("/api/version", () =>
{
    var assembly = System.Reflection.Assembly.GetEntryAssembly()!;
    var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
        Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
    var informationalVersion = attr?.InformationalVersion ?? "";
    var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
    var version = plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    var fullCommit = plusIndex >= 0 ? informationalVersion[(plusIndex + 1)..] : "";
    var commit = fullCommit.Length > 8 ? fullCommit[..8] : fullCommit;

    return Results.Ok(new { version, commit, informationalVersion });
}).AllowAnonymous();

// Hangfire dashboard (admin only in production).
// Skipped in Testing — MapHangfireDashboard resolves JobStorage from DI eagerly,
// and Hangfire's static JobStorage.Current isn't set until after migrations.
if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = app.Environment.IsDevelopment()
            ? []
            : [new Humans.Web.HangfireAuthorizationFilter()]
    });
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();
app.MapHub<CityPlanningHub>("/hubs/city-planning");

// Run database migrations on startup (must happen before Hangfire job registration
// because Hangfire needs its tables to exist for distributed lock acquisition)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
    var migrationLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");
    var dbName = dbContext.Database.GetDbConnection().Database;

    try
    {
        var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToList();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();

        migrationLogger.LogInformation(
            "Database {Database}: {AppliedCount} applied migrations, {PendingCount} pending",
            dbName, applied.Count, pending.Count);

        if (pending.Count > 0)
        {
            foreach (var migration in pending)
                migrationLogger.LogInformation("Pending migration: {Migration}", migration);

            await dbContext.Database.MigrateAsync();

            var nowApplied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
            migrationLogger.LogInformation(
                "Database {Database}: migrations complete — {AppliedCount} total applied",
                dbName, nowApplied.Count);
        }
        else
        {
            migrationLogger.LogInformation("Database {Database}: schema is up to date", dbName);
        }
    }
    catch (Exception ex)
    {
        migrationLogger.LogError(ex,
            "Database migration failed for {Database}. The application may not function correctly",
            dbName);
        throw;
    }
}

if (!app.Environment.IsEnvironment("Testing"))
{
    // Force Hangfire global configuration to initialize (sets JobStorage.Current)
    // before registering recurring jobs. The AddHangfire((sp, config) => ...) overload
    // defers the config lambda until IGlobalConfiguration is resolved from DI;
    // RecurringJob.AddOrUpdate() uses the static JobStorage.Current, so we must
    // ensure it's set first.
    app.Services.GetRequiredService<IGlobalConfiguration>();
    app.UseHumansRecurringJobs();
}

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static async Task WriteDetailedHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    var result = new
    {
        status = report.Status.ToString(),
        results = report.Entries.ToDictionary(
            e => e.Key,
            e => new
            {
                status = e.Value.Status.ToString(),
                duration = e.Value.Duration.ToString()
            },
            StringComparer.Ordinal)
    };

    await context.Response.WriteAsJsonAsync(result);
}

// Make Program accessible to WebApplicationFactory<Program> in integration tests
public partial class Program;
