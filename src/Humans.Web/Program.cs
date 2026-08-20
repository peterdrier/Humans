using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authorization;
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
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Humans.Base.Data;
using Humans.Base.Hosting;
using Humans.Web.Services;
using Humans.Web.Authorization;
using Humans.Web.Health;
using Humans.Web.Middleware;
using Microsoft.Extensions.Localization;
using Npgsql;
using Humans.Base.Logging;
using Humans.Base.Extensions;
using Serilog;
using Serilog.Events;
using Humans.Web.Hosting;
using Humans.Web.ModelBinders;
using Humans.Web.Data;
using Humans.Users.Contracts;
using Humans.Web.Localization;

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

// The three dev fixture seeders moved into src/Sections/Humans.Development at G5
// (nobodies-collective/Humans#866) and are internal there, so Shell can no longer name them.
// Their non-Production gate moved with them into that section's Section.Register, which reads
// HostDefaults.EnvironmentKey off the configuration passed to it and fails closed.

// All environments: the per-source-IP sign-in failure throttle for /Account/GateLogin.
// Shell's: the /Account/GateLogin page belongs to Auth. The gate-terminal account seeder
// moved to Humans.Tickets with /Tickets/Admin/Gate (nobodies-collective/Humans#1091).
builder.Services.AddSingleton<GateLoginThrottle>();

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
builder.Services.AddSingleton<TrackingMemoryCache>(_ =>
    new TrackingMemoryCache(new MemoryCache(new MemoryCacheOptions())));
builder.Services.AddSingleton<IMemoryCache>(sp => sp.GetRequiredService<TrackingMemoryCache>());
builder.Services.AddSingleton<ICacheStatsProvider>(sp => sp.GetRequiredService<TrackingMemoryCache>());

// EF/factory/migrations wired in Infrastructure so the section DbContexts stay internal — see #750.
builder.Services.AddHumansPersistence();

// Persist DataProtection keys to DB so auth cookies survive container restarts.
builder.Services.AddDataProtection()
    .PersistKeysToSystemDbContext()
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
    // 14-day sliding window — any visit inside it extends it, 14 days away requires re-login.
    // Paired with isPersistent: true at every sign-in site, without which the cookie has no
    // Expires and dies on browser close. Revocation stays per-request regardless of cookie age:
    // MembershipRequiredFilter re-checks UserState, the claims transform re-derives roles, and
    // Identity's security stamp invalidates server-side — see nobodies-collective#925.
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
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

                // Warning + client IP so /Debug/Logs traces user-reported sign-in issues — see #483.
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

// Skip Hangfire entirely in Testing — AddHangfire registers IBackgroundJobClient
// with a factory that reads JobStorage.Current at resolution time. Without storage
// configured, every DI-graph build that transitively touches IBackgroundJobClient
// (e.g. HangfireImmediateOutboxProcessor → IImmediateOutboxProcessor →
// OutboxEmailService) throws InvalidOperationException, failing every integration
// test. HumansWebApplicationFactory binds a substitute IBackgroundJobClient in
// Testing — see docs/testing/test-system-reliability.md (P0/#762).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire((sp, config) =>
    {
        config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings();

        config.UsePostgreSqlStorage(options =>
            options.UseNpgsqlConnection(
                sp.GetRequiredService<IConfiguration>()
                    .GetConnectionString("DefaultConnection")!),
            new PostgreSqlStorageOptions
            {
                DistributedLockTimeout = TimeSpan.FromSeconds(5)
            });
    });

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

// HealthCheckTags.External tags third-party reachability checks — sections apply it to their
// own checks below. They surface on /health for diagnostics but are excluded from
// /health/ready — a vendor outage must never fail the readiness probe and block or roll back
// a deploy.
var healthChecks = builder.Services.AddHealthChecks()
    .AddNpgSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), name: "postgresql")
    .AddCheck<ConfigurationHealthCheck>("configuration");

// Sections add their own checks; the names are monitoring keys, so they stay with the owner.
foreach (var contributor in SectionDiscoveryExtensions.DiscoverImplementations<ISectionHealthChecks>())
{
    contributor.AddHealthChecks(healthChecks, builder.Configuration);
}

// Hangfire health check reads JobStorage.Current; only register it when
// the rest of the Hangfire stack is wired (i.e. outside Testing).
if (!builder.Environment.IsEnvironment("Testing"))
{
    healthChecks.AddHangfire(options => options.MinimumAvailableServers = 1, name: "hangfire");
}

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

builder.Services.AddSingleton(sp => new RateLimitRejectionAggregator(
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting")));

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
        // The rejection short-circuits before ClientStatsMiddleware, so record the
        // 429 in the /Debug/HttpErrors buffer here.
        context.HttpContext.RequestServices.GetRequiredService<IClientStatsTracker>()
            .RecordError(ClientStatsMiddleware.BuildEntry(
                context.HttpContext, StatusCodes.Status429TooManyRequests));

        var remoteIp = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        var authenticatedUser = context.HttpContext.User.Identity?.IsAuthenticated == true
            ? context.HttpContext.User.Identity.Name
            : null;
        var identity = authenticatedUser ?? remoteIp ?? "anonymous";

        // A bot sweep produces dozens of near-identical rejections; only the
        // first per source IP per window logs in detail (and pays the reverse-DNS
        // lookup) — the rest are counted and flushed as one summary line. Keyed
        // by IP only: the display identity (burner name) is not unique.
        if (!context.HttpContext.RequestServices.GetRequiredService<RateLimitRejectionAggregator>()
                .RecordRejection(remoteIp ?? "unknown"))
        {
            return;
        }

        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RateLimiting");

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
        // Name-gate runs before the membership gate: a freshly-created OAuth /
        // imported account with a blank BurnerName must be sent to the name form
        // before MembershipRequiredFilter bounces it to Guest/Home. See #812.
        options.Filters.Add<NameRequiredFilter>();
        options.Filters.Add<MembershipRequiredFilter>();
        options.Filters.Add<Humans.Web.Filters.AuthorizationPillFilter>();

        // datetime-local inputs post without seconds; NodaTime's LocalDateTime
        // TypeConverter requires them. See LocalDateTimeModelBinder
        // (nobodies-collective/Humans#932).
        options.ModelBinderProviders.Insert(0, new LocalDateTimeModelBinderProvider());
    })
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
    {
        // Route every [Display]/[Required]/[StringLength] lookup through SharedResource
        // (same resx views/controllers already use) instead of the per-type resource
        // MVC defaults to (which nothing here provides, so annotations rendered raw
        // English regardless of culture). A key with no SharedResource match just
        // falls back to the attribute's own text, so untouched view models are unaffected.
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(Humans.Base.SharedResource));
    });

// A section project's controllers are internal (nobodies-collective/Humans#866); MVC's
// default provider only discovers public ones, and says nothing when it doesn't.
mvcBuilder.ConfigureApplicationPartManager(apm =>
    apm.FeatureProviders.Add(new SectionControllerFeatureProvider()));

// …and the same for a section's view components, which MVC discovers through a separate,
// equally public-only convention (Notifications' bell).
mvcBuilder.ConfigureApplicationPartManager(apm =>
    apm.FeatureProviders.Add(new SectionViewComponentFeatureProvider()));

// DevLoginController depends on DevPersonaSeeder (non-Production only); exclude in Prod so
// ValidateOnBuild passes and /dev/login/* 404s cleanly. Must be added after
// SectionControllerFeatureProvider above, which is what puts the internal controller in the
// list in the first place.
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
            var userIdRaw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userIdRaw, out var userId))
            {
                var users = context.RequestServices.GetRequiredService<IUserServiceRead>();
                var user = await users.GetUserInfoAsync(userId, context.RequestAborted);
                if (user is not null && !string.IsNullOrEmpty(user.PreferredLanguage))
                {
                    return new ProviderCultureResult(culture: "en", uiCulture: user.PreferredLanguage);
                }
            }
        }
        return null;
    }));

    // Wrap every provider (the preference-based one above, plus the default query
    // string/cookie/Accept-Language providers) so the parsing culture is always "en"
    // regardless of which one wins — see UiCultureOnlyRequestCultureProvider (#1067).
    options.RequestCultureProviders = options.RequestCultureProviders
        .Select(provider => (IRequestCultureProvider)new UiCultureOnlyRequestCultureProvider(provider))
        .ToList();
});

var app = builder.Build();

// Wire IHttpContextAccessor so the ambient Instant display formatters pick up session timezone.
DateTimeDisplayExtensions.Initialize(app.Services.GetRequiredService<IHttpContextAccessor>());

// Post-Build so the parameterless enricher activator can read ambient HttpContext per log emission.
CurrentUserEnricher.StaticAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();

// HumansMetricsService is registered AddHostedService — its gauge-refresh timer is armed in
// StartAsync (after the migration barrier), so no eager pre-Run() resolve is needed here.

// Localization diagnostic check
{
    using var scope = app.Services.CreateScope();
    var localizerFactory = scope.ServiceProvider.GetRequiredService<IStringLocalizerFactory>();
    var resourceType = typeof(Humans.Base.SharedResource);
    var localizer = localizerFactory.Create(resourceType);
    var testKey = "Dashboard_Welcome";
    var result = localizer[testKey];

    if (result.ResourceNotFound)
    {
        Log.Error("LOCALIZATION BROKEN: Resource key '{Key}' not found. SearchedLocation: {Location}",
            testKey, result.SearchedLocation);
        Log.Error("Resource type: {TypeName}, Assembly: {Assembly}",
            resourceType.FullName, resourceType.Assembly.GetName().Name);

        // List embedded resources for debugging
        var assembly = resourceType.Assembly;
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

// Same check for every section's own resource set (nobodies-collective/Humans#866
// design §3). A section's .resx manifest name derives from the adjacent .cs file's
// namespace, not its folder path, and getting that wrong is silent — every string in
// the set renders as its raw key. Checking that the manifest the localizer will look
// for is actually embedded needs no key-name convention and no culture, so a new
// section adds nothing here.
foreach (var resourceType in SectionDiscoveryExtensions.SectionResourceTypes())
{
    var expected = resourceType.FullName + ".resources";
    var embedded = resourceType.Assembly.GetManifestResourceNames();
    if (!embedded.Contains(expected, StringComparer.Ordinal))
    {
        Log.Error(
            "LOCALIZATION BROKEN: {Assembly} embeds no '{Expected}'. Its .resx files must sit " +
            "in the same folder as {TypeName}, whose namespace decides the manifest name. Found: {Embedded}",
            resourceType.Assembly.GetName().Name, expected, resourceType.FullName,
            string.Join(", ", embedded));
    }
    else
    {
        Log.Information("Localization OK: {Expected} embedded in {Assembly}",
            expected, resourceType.Assembly.GetName().Name);
    }
}

// Authorization-policy diagnostic check (nobodies-collective/Humans#1076): every
// PolicyNames constant must resolve to a policy actually registered — by Shell or by a
// section's ISectionPolicies. A section that stops registering its policy (e.g. turned
// off) must fail loud here, not 403 mysteriously wherever the policy is used.
{
    using var scope = app.Services.CreateScope();
    var policyProvider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
    var policyNames = typeof(Humans.Base.Authorization.PolicyNames)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!));

    foreach (var (name, value) in policyNames)
    {
        var policy = await policyProvider.GetPolicyAsync(value);
        if (policy is null)
        {
            Log.Error(
                "AUTHORIZATION BROKEN: PolicyNames.{Name} has no registered policy. Its " +
                "owning section may be missing, or its Policies contribution failed to register.",
                name);
        }
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

// "no-cache" = cache but always revalidate (cheap 304s at this scale). Without an explicit policy,
// browsers heuristically cache static files (~10% of time since Last-Modified) with NO request at
// all, so after a release some long-running kiosk tablets kept serving week-old CSS/JS while others
// didn't. Fingerprinted URLs (asp-append-version / FileVersionProvider) change per release, but
// unfingerprinted paths — and nested JS module imports, which no tag helper can reach — need this.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

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

// Tally page views by client (OS/browser/device) for /Admin/ClientStats. Runs
// after the response is produced; only text/html responses are counted.
app.UseMiddleware<ClientStatsMiddleware>();

// Dev-loop guard, not a runtime safety net — never Production/Testing. See #1055.
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    app.UseMiddleware<ViewComponentTagSurvivalMiddleware>();
}

app.UseAuthorization();

// Before MVC runs: a malformed TempData cookie throws an unloggable-context
// FormatException deep in CookieTempDataProvider — see #1038. Catch it here where
// Path/UserAgent are still available, and strip the cookie so MVC never sees it.
app.UseMiddleware<TempDataCookieValidationMiddleware>();

app.UseSession();

// Hard route restriction for the shared gate-terminal kiosk account: it may only reach
// the gate terminal and its own login/logout. Defense in depth on top of its zero
// roles/teams — even if a URL were typed on the device, the kiosk account is bounced
// to /Gate and can never browse the rest of Humans.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && context.User.HasClaim(
            ClaimTypes.NameIdentifier,
            Humans.Base.Constants.SystemUserIds.GateTerminal.ToString()))
    {
        var path = context.Request.Path;
        var allowed =
            path.StartsWithSegments("/Gate", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/Account/GateLogin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/Account/Logout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            context.Response.Redirect("/Gate");
            return;
        }
    }
    await next();
});

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
    // Readiness check - confirms our own stack (DB, config, Hangfire) is available.
    // Third-party reachability (HealthCheckTags.External) is diagnostic-only on /health.
    Predicate = r => !r.Tags.Contains(HealthCheckTags.External)
});

app.MapPrometheusScrapingEndpoint("/metrics");

// Anonymous beacon: browsers report their screen resolution (the one client stat
// not available from request headers) for /Admin/ClientStats. Fire-and-forget.
app.MapPost("/api/client-metrics", (
        Humans.Web.Models.ClientMetricsBeacon beacon,
        IClientStatsTracker clientStats) =>
    {
        clientStats.RecordResolution(beacon.ScreenWidth, beacon.ScreenHeight);
        return Results.NoContent();
    })
    .AllowAnonymous();

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

// Sections map what routing cannot discover on its own — hubs and the like.
foreach (var contributor in SectionDiscoveryExtensions.DiscoverImplementations<ISectionEndpoints>())
{
    contributor.MapEndpoints(app);
}

// DB migrations run via DatabaseMigrationHostedService during StartAsync, before Hangfire takes locks.

// Widened to also cover the Hangfire block below (nobodies-collective/Humans#1060): a preview
// env whose database doesn't exist yet fails here first — every recurring-job registration
// throws the same connection error, but each is caught and logged as a WARNING by
// UseHumansRecurringJobs (a legitimate concern there: a stale distributed lock must not stop
// the app booting). That warning is loud but incidental; the real fatal cause is whichever of
// this block or DatabaseMigrationHostedService.StartingAsync (run inside RunAsync) throws
// first, and previously only the RunAsync half was caught here.
try
{
    if (!app.Environment.IsEnvironment("Testing"))
    {
        // Force IGlobalConfiguration resolution so JobStorage.Current is set before RecurringJob.AddOrUpdate uses it.
        app.Services.GetRequiredService<IGlobalConfiguration>();
        app.UseHumansRecurringJobs();
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    LogStartupFailure(ex, builder.Configuration);
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// One clear FATAL line for a startup crash, naming the database when the cause is a
/// Postgres connectivity failure (nobodies-collective/Humans#1060) — e.g. a PR preview
/// deployed before <c>preview-db.yml</c> cloned <c>humans_pr_{N}</c>. Falls back to the
/// generic message for anything else, so a startup failure is never silently unlabeled.
/// </summary>
static void LogStartupFailure(Exception ex, IConfiguration configuration)
{
    var pgEx = FindException<NpgsqlException>(ex);
    if (pgEx is null)
    {
        Log.Fatal(ex, "Application terminated unexpectedly");
        return;
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection");
    var database = connectionString is not null
        ? new NpgsqlConnectionStringBuilder(connectionString).Database
        : null;

    if (pgEx is PostgresException { SqlState: PostgresErrorCodes.InvalidCatalogName })
    {
        Log.Fatal(ex, "Application terminated unexpectedly: database {Database} does not exist", database);
    }
    else
    {
        Log.Fatal(ex, "Application terminated unexpectedly: database {Database} is unreachable", database);
    }
}

/// <summary>Walks <paramref name="ex"/>'s InnerException chain (and AggregateException branches) for the first match.</summary>
static T? FindException<T>(Exception? ex) where T : Exception
{
    for (; ex is not null; ex = ex.InnerException)
    {
        if (ex is T match)
            return match;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var found = FindException<T>(inner);
                if (found is not null)
                    return found;
            }
        }
    }

    return null;
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
