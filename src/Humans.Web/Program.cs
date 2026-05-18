using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.ResponseCompression;
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
using Humans.Infrastructure.Hosting;
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

var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Humans.Web")
    .Enrich.With<PiiRedactionEnricher>()
    .Enrich.With<CurrentUserEnricher>()
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

// Fail fast on DI cycles/captive deps; factory lambdas still need smoke coverage.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Concrete instance — used during startup config before DI is built.
var configRegistry = new ConfigurationRegistry();
builder.Services.AddSingleton(configRegistry);

builder.Services.AddSingleton<IClock>(SystemClock.Instance);
if (!builder.Environment.IsProduction())
{
    builder.Services.AddScoped<DevelopmentBudgetSeeder>();
    builder.Services.AddScoped<DevelopmentCampRoleSeeder>();
    builder.Services.AddScoped<DevelopmentDashboardSeeder>();
    builder.Services.AddScoped<DevPersonaSeeder>();
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
});

builder.Configuration.GetRequiredSetting(
    configRegistry, "ConnectionStrings:DefaultConnection", "Database", isSensitive: true);

// Singleton so conn string resolves at service-resolution time (lets integration tests override via WebApplicationFactory).
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

builder.Services.AddSingleton<QueryStatistics>();
builder.Services.AddSingleton<QueryMonitoringInterceptor>();

// TrackingMemoryCache decorates MemoryCache for per-key hit/miss stats; exposed as both IMemoryCache and ICacheStatsProvider.
builder.Services.AddSingleton<TrackingMemoryCache>(sp =>
    new TrackingMemoryCache(new MemoryCache(new MemoryCacheOptions())));
builder.Services.AddSingleton<IMemoryCache>(sp => sp.GetRequiredService<TrackingMemoryCache>());
builder.Services.AddSingleton<ICacheStatsProvider>(sp => sp.GetRequiredService<TrackingMemoryCache>());

// EF/factory/migrations wired in Infrastructure so HumansDbContext stays internal — see #750.
builder.Services.AddHumansPersistence(builder.Environment.IsDevelopment());

// Persist DataProtection keys to DB so auth cookies survive container restarts.
builder.Services.AddDataProtection()
    .PersistKeysToHumansDbContext()
    .SetApplicationName("Humans.Web");

builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
    {
        // UserEmail table owns email uniqueness; User.Email is null on new users.
        options.User.RequireUniqueEmail = false;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddHumansEntityFrameworkStores()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<HumansUserClaimsPrincipalFactory>();

// Magic link tokens use DataProtection (15-min lifetime), not Identity token providers.

// TLS terminated by Coolify/reverse proxy.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

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
        // MapJsonKey surfaces Google's email_verified as a claim — see #697.
        Microsoft.AspNetCore.Authentication.ClaimActionCollectionMapExtensions
            .MapJsonKey(options.ClaimActions, "email_verified", "email_verified", "boolean");
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

                // Warning + client IP so /Admin/Logs traces user-reported sign-in issues — see #483.
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
                else if (context.Failure is OperationCanceledException)
                {
                    // User closed tab / network dropped mid-callback — see #728.
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

// Canonical policies — see docs/authorization-inventory.md.
builder.Services.AddHumansAuthorizationPolicies();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleAssignmentClaimsTransformation>();

// /Profile/Me/ImportGooglePhoto avatar fetch — short timeout, surface errors instead of hanging.
builder.Services.AddHttpClient("GoogleAvatar", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHangfire((sp, config) =>
{
    config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();

    // Skip in Testing — Hangfire's per-AppDomain static state conflicts with parallel WebApplicationFactory + Testcontainers.
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

builder.Services.AddSingleton(new ActivitySource(serviceName, serviceVersion));

builder.Services.AddHealthChecks()
    .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgresql")
    .AddHangfire(options => options.MinimumAvailableServers = 1, name: "hangfire")
    .AddCheck<ConfigurationHealthCheck>("configuration")
    .AddCheck<SmtpHealthCheck>("smtp")
    .AddCheck<GitHubHealthCheck>("github")
    .AddCheck<GoogleWorkspaceHealthCheck>("google-workspace")
    .AddCheck<AnthropicHealthCheck>("anthropic-api-reachable");

builder.Services.AddHumansInfrastructure(builder.Configuration, builder.Environment, configRegistry);

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

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Avoid cascading rate-limits via error page re-entry.
        if (context.Request.Path.StartsWithSegments("/Home/Error", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        if (context.Request.Path == "/favicon.ico")
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        // List pages legitimately load ~30 profile images at once.
        if (context.Request.Path.StartsWithSegments("/Profile/Picture", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        // SignalR long-polling trivially exceeds 100/min; hub manages own backpressure + auth.
        if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        // e2e tests and internal tooling run from 192.168.*
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (remoteIp is not null && remoteIp.StartsWith("192.168.", StringComparison.Ordinal))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
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

// Forwarded headers enabled via ASPNETCORE_FORWARDEDHEADERS_ENABLED=true in deployment env.

// Session backs browser-detected timezone.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddLocalization();

// BarriosPublic: nobodies.team + localhost dev for /api/barrios. EventsApi: open for PWA /api/events.
builder.Services.AddCors(options =>
{
    options.AddPolicy("BarriosPublic", policy =>
    {
        // SetIsOriginAllowed overrides WithOrigins; lambda must cover all allowed origins.
        policy.SetIsOriginAllowed(origin =>
                origin.StartsWith("http://localhost:", StringComparison.Ordinal) ||
                origin.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) ||
                string.Equals(origin, "https://nobodies.team", StringComparison.Ordinal) ||
                string.Equals(origin, "https://www.nobodies.team", StringComparison.Ordinal))
            .WithMethods("GET")
            .WithHeaders("Content-Type", "Accept");
    });
    options.AddPolicy("EventsApi", policy =>
    {
        policy.AllowAnyOrigin()
            .WithMethods("GET")
            .WithHeaders("Content-Type", "Accept");
    });
});

var mvcBuilder = builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add<MembershipRequiredFilter>();
        options.Filters.Add<Humans.Web.Filters.AuthorizationPillFilter>();
    })
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization();

// DevLoginController depends on DevPersonaSeeder (non-Production only); exclude in Prod so ValidateOnBuild passes and /dev/login/* 404s cleanly.
if (builder.Environment.IsProduction())
{
    mvcBuilder.ConfigureApplicationPartManager(apm =>
        apm.FeatureProviders.Add(new DevLoginControllerExclusionProvider()));
}
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

// In Developement, compile Razor pages each time they are loaded
if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
}

// Order matters — Cancellation handler FIRST so client-abort OCEs don't log at Error; GlobalLogging returns false so /Home/Error still renders.
builder.Services.AddExceptionHandler<Humans.Web.ExceptionHandlers.CancellationExceptionHandler>();
builder.Services.AddExceptionHandler<Humans.Web.ExceptionHandlers.GlobalLoggingExceptionHandler>();

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

// Wire IHttpContextAccessor so Instant.ToDisplay*() picks up session timezone.
DateTimeDisplayExtensions.Initialize(app.Services.GetRequiredService<IHttpContextAccessor>());

// Post-Build so the parameterless enricher activator can read ambient HttpContext per log emission.
CurrentUserEnricher.StaticAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();

// Eager resolve so the background gauge-refresh timer starts immediately.
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

// Must be first (reverse proxy).
app.UseForwardedHeaders();

// Must wrap UseExceptionHandler so handlers swap status before Serilog records — see #728.
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // Runs the AddExceptionHandler<T>() pipeline, then re-executes at /Home/Error if none short-circuit.
    app.UseExceptionHandler("/Home/Error");
}

app.UseStatusCodePagesWithReExecute("/Home/Error/{0}");

if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

// Block direct /uploads/profile-pictures/ before UseStaticFiles — must go through /Profile/Picture/{id} for GDPR anonymization gate.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path is not null &&
        path.StartsWith("/uploads/profile-pictures/", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

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

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "geolocation=(), microphone=(), camera=(self)");
    await next();
});

app.UseMiddleware<CspNonceMiddleware>();

app.UseRouting();

app.UseCors();

app.UseRateLimiter();

app.UseAuthentication();

// Between Authentication and Authorization so the principal is populated AND denied-but-authenticated requests (403s short-circuited by UseAuthorization) still count toward humans.active_users.
app.UseMiddleware<UserActivityTrackingMiddleware>();

app.UseAuthorization();

app.UseSession();

app.UseRequestLocalization();

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

app.MapPrometheusScrapingEndpoint("/metrics");

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

// Admin-only in prod. Skipped in Testing — JobStorage.Current isn't set until after migrations.
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

// DB migrations run via DatabaseMigrationHostedService during StartAsync, before Hangfire takes locks.

if (!app.Environment.IsEnvironment("Testing"))
{
    // Force IGlobalConfiguration resolution so JobStorage.Current is set before RecurringJob.AddOrUpdate uses it.
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
