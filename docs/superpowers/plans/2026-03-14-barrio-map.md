# Barrio Map Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an interactive satellite map where barrio leads place camp footprints per season, city planners have live collaborative visibility, and CampAdmin controls placement phases.

**Architecture:** Three new domain entities (`CampPolygon`, `CampPolygonHistory`, `CampMapSettings`) with EF Core PostgreSQL text-column GeoJSON storage; `ICampMapService`/`CampMapService` in the service layer; `CampMapHub` for SignalR real-time cursor presence; MVC `BarrioMapController` for pages + `CampMapApiController` for JSON API; MapLibre GL JS + maplibre-gl-draw + Turf.js in the frontend.

**Tech Stack:** ASP.NET Core SignalR (SDK-bundled, no extra NuGet), MapLibre GL JS 4.7.1 (CDN), @maplibre/maplibre-gl-draw 1.4.0 (CDN), Turf.js 7.1.0 (CDN), @microsoft/signalr 8.0.7 (CDN), EF Core + PostgreSQL (`text` GeoJSON columns), NodaTime `Instant`.

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `src/Humans.Domain/Entities/CampPolygon.cs` | Current polygon per camp season |
| Create | `src/Humans.Domain/Entities/CampPolygonHistory.cs` | Append-only polygon version log |
| Create | `src/Humans.Domain/Entities/CampMapSettings.cs` | Per-year placement phase + limit zone |
| Create | `src/Humans.Infrastructure/Data/Configurations/CampPolygonConfiguration.cs` | EF config: unique FK, text column, Restrict deletes |
| Create | `src/Humans.Infrastructure/Data/Configurations/CampPolygonHistoryConfiguration.cs` | EF config: composite index, append-only |
| Create | `src/Humans.Infrastructure/Data/Configurations/CampMapSettingsConfiguration.cs` | EF config: unique year index |
| Modify | `src/Humans.Infrastructure/Data/HumansDbContext.cs` | Add 3 new DbSets |
| Create | `src/Humans.Infrastructure/Configuration/CampMapOptions.cs` | `CityPlanningTeamSlug` config class |
| Create | `src/Humans.Application/Interfaces/ICampMapService.cs` | Service interface + DTOs |
| Create | `src/Humans.Infrastructure/Services/CampMapService.cs` | All map service logic |
| Modify | `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` | Register `ICampMapService` + `CampMapOptions` |
| Create | `src/Humans.Web/Hubs/CampMapHub.cs` | SignalR hub: cursor relay + disconnect |
| Create | `src/Humans.Web/Controllers/CampMapApiController.cs` | JSON API: state, save, restore, history, export |
| Create | `src/Humans.Web/Controllers/BarrioMapController.cs` | MVC pages: Index + Admin + placement POSTs |
| Modify | `src/Humans.Web/Program.cs` | `AddSignalR()` + `MapHub<CampMapHub>` |
| Modify | `src/Humans.Web/Middleware/CspNonceMiddleware.cs` | Add `worker-src blob:`, ESRI + SignalR to CSP |
| Modify | `src/Humans.Web/appsettings.json` | Add `CampMap` config section |
| Create | `src/Humans.Web/Views/BarrioMap/Index.cshtml` | Map page with MapLibre + JS |
| Create | `src/Humans.Web/Views/BarrioMap/Admin.cshtml` | Admin panel: placement phase + limit zone |
| Modify | `src/Humans.Web/Views/Home/About.cshtml` | Add CDN library attributions |
| Create | `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs` | Service unit tests |
| Create | (EF migration via CLI) | `AddCampMap` migration |

---

## Chunk 1: Domain + Data Layer

### Task 1: Domain Entities

**Files:**
- Create: `src/Humans.Domain/Entities/CampPolygon.cs`
- Create: `src/Humans.Domain/Entities/CampPolygonHistory.cs`
- Create: `src/Humans.Domain/Entities/CampMapSettings.cs`

- [ ] **Step 1: Create `CampPolygon.cs`**

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class CampPolygon
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public string GeoJson { get; set; } = string.Empty;
    public double AreaSqm { get; set; }

    public Guid LastModifiedByUserId { get; set; }
    public User LastModifiedByUser { get; set; } = null!;

    public Instant LastModifiedAt { get; set; }
}
```

- [ ] **Step 2: Create `CampPolygonHistory.cs`**

All fields are `init` — this entity is append-only and never updated.

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class CampPolygonHistory
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public string GeoJson { get; init; } = string.Empty;
    public double AreaSqm { get; init; }

    public Guid ModifiedByUserId { get; init; }
    public User ModifiedByUser { get; set; } = null!;

    public Instant ModifiedAt { get; init; }

    /// <summary>"Saved" by default; "Restored from 2026-03-10T14:32:05Z" for restores.</summary>
    public string Note { get; init; } = "Saved";
}
```

- [ ] **Step 3: Create `CampMapSettings.cs`**

One row per year, created on demand.

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class CampMapSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The season year this row applies to. Unique.</summary>
    public int Year { get; init; }

    public bool IsPlacementOpen { get; set; }
    public Instant? OpenedAt { get; set; }
    public Instant? ClosedAt { get; set; }

    /// <summary>GeoJSON FeatureCollection defining the visual site boundary. Null until uploaded.</summary>
    public string? LimitZoneGeoJson { get; set; }

    public Instant UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Verify project builds**

```bash
dotnet build src/Humans.Domain/Humans.Domain.csproj
```

Expected: Build succeeded, 0 errors.

---

### Task 2: EF Core Configurations

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/CampPolygonConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/CampPolygonHistoryConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/CampMapSettingsConfiguration.cs`

- [ ] **Step 1: Create `CampPolygonConfiguration.cs`**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampPolygonConfiguration : IEntityTypeConfiguration<CampPolygon>
{
    public void Configure(EntityTypeBuilder<CampPolygon> builder)
    {
        builder.ToTable("camp_polygons");

        // One polygon per camp season
        builder.HasIndex(p => p.CampSeasonId).IsUnique();

        builder.Property(p => p.GeoJson).HasColumnType("text").IsRequired();

        builder.HasOne(p => p.CampSeason)
            .WithMany()
            .HasForeignKey(p => p.CampSeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.LastModifiedByUser)
            .WithMany()
            .HasForeignKey(p => p.LastModifiedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

- [ ] **Step 2: Create `CampPolygonHistoryConfiguration.cs`**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampPolygonHistoryConfiguration : IEntityTypeConfiguration<CampPolygonHistory>
{
    public void Configure(EntityTypeBuilder<CampPolygonHistory> builder)
    {
        builder.ToTable("camp_polygon_histories");

        builder.HasIndex(h => new { h.CampSeasonId, h.ModifiedAt });

        builder.Property(h => h.GeoJson).HasColumnType("text").IsRequired();
        builder.Property(h => h.Note).HasMaxLength(512).IsRequired();

        builder.HasOne(h => h.CampSeason)
            .WithMany()
            .HasForeignKey(h => h.CampSeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(h => h.ModifiedByUser)
            .WithMany()
            .HasForeignKey(h => h.ModifiedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

- [ ] **Step 3: Create `CampMapSettingsConfiguration.cs`**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class CampMapSettingsConfiguration : IEntityTypeConfiguration<CampMapSettings>
{
    public void Configure(EntityTypeBuilder<CampMapSettings> builder)
    {
        builder.ToTable("camp_map_settings");

        builder.HasIndex(s => s.Year).IsUnique();

        builder.Property(s => s.LimitZoneGeoJson).HasColumnType("text");
    }
}
```

---

### Task 3: DbContext DbSets

**Files:**
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Add three DbSets after the existing `CampSettings` DbSet (line 48)**

```csharp
    public DbSet<CampPolygon> CampPolygons => Set<CampPolygon>();
    public DbSet<CampPolygonHistory> CampPolygonHistories => Set<CampPolygonHistory>();
    public DbSet<CampMapSettings> CampMapSettings => Set<CampMapSettings>();
```

Note: `CampMapSettings` (DbSet) has the same name as `CampMapSettings` (entity class). EF resolves this correctly — the property type `DbSet<CampMapSettings>` distinguishes them.

- [ ] **Step 2: Build Infrastructure project**

```bash
dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj
```

Expected: Build succeeded, 0 errors.

---

### Task 4: Migration

- [ ] **Step 1: Generate migration**

```bash
dotnet ef migrations add AddCampMap \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web
```

Expected: Migration `AddCampMap` created under `src/Humans.Infrastructure/Migrations/`.

- [ ] **Step 2: Inspect the generated migration**

Open the generated `.cs` file and verify it contains:
- `CreateTable("camp_polygons", ...)` with unique index on `camp_season_id`
- `CreateTable("camp_polygon_histories", ...)` with composite index
- `CreateTable("camp_map_settings", ...)` with unique index on `year`
- FK constraints with `onDelete: ReferentialAction.Restrict` on the non-nullable FKs

If anything looks wrong, run `dotnet ef migrations remove` and fix the entity/config before regenerating.

- [ ] **Step 3: Apply migration to local dev DB**

```bash
dotnet ef database update \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web
```

Expected: `Done.`

- [ ] **Step 4: Commit Chunk 1**

```bash
git add src/Humans.Domain/Entities/CampPolygon.cs \
        src/Humans.Domain/Entities/CampPolygonHistory.cs \
        src/Humans.Domain/Entities/CampMapSettings.cs \
        src/Humans.Infrastructure/Data/Configurations/CampPolygonConfiguration.cs \
        src/Humans.Infrastructure/Data/Configurations/CampPolygonHistoryConfiguration.cs \
        src/Humans.Infrastructure/Data/Configurations/CampMapSettingsConfiguration.cs \
        src/Humans.Infrastructure/Data/HumansDbContext.cs \
        src/Humans.Infrastructure/Migrations/
git commit -m "feat: add CampPolygon, CampPolygonHistory, CampMapSettings entities and migration"
```

---

## Chunk 2: Service Layer

### Task 5: Config Class + appsettings

**Files:**
- Create: `src/Humans.Infrastructure/Configuration/CampMapOptions.cs`
- Modify: `src/Humans.Web/appsettings.json`

- [ ] **Step 1: Create `CampMapOptions.cs`**

```csharp
namespace Humans.Infrastructure.Configuration;

public class CampMapOptions
{
    public const string SectionName = "CampMap";

    /// <summary>
    /// Slug of the Team that has full map admin access (city planning team).
    /// Members of this team can always edit polygons and access the admin panel.
    /// </summary>
    public string CityPlanningTeamSlug { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Add config section to `appsettings.json`**

Add after `"AllowedHosts"`:

```json
  "CampMap": {
    "CityPlanningTeamSlug": "city-planning"
  }
```

---

### Task 6: Interface + DTOs

**Files:**
- Create: `src/Humans.Application/Interfaces/ICampMapService.cs`

- [ ] **Step 1: Create `ICampMapService.cs`**

```csharp
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface ICampMapService
{
    // Queries
    Task<List<CampPolygonDto>> GetPolygonsAsync(int year, CancellationToken cancellationToken = default);
    Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutPolygonAsync(int year, CancellationToken cancellationToken = default);
    Task<List<PolygonHistoryEntryDto>> GetPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<Guid?> GetUserCampSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default);

    // Writes
    Task<(CampPolygon polygon, CampPolygonHistory history)> SavePolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default);

    Task<(CampPolygon polygon, CampPolygonHistory history)> RestorePolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default);

    // Authorization
    Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<bool> IsUserMapAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    // Settings (creates row on demand for PublicYear)
    Task<CampMapSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task UpdateLimitZoneAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default);

    // Export
    Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default);
}

public record CampPolygonDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm);

public record CampSeasonSummaryDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug);

public record PolygonHistoryEntryDto(
    Guid Id,
    string ModifiedByDisplayName,
    string ModifiedAt,
    double AreaSqm,
    string Note);

public record SavePolygonRequest(string GeoJson, double AreaSqm);
```

---

### Task 7: Service Implementation

**Files:**
- Create: `src/Humans.Infrastructure/Services/CampMapService.cs`

- [ ] **Step 1: Write failing test first** (see Task 8 — write tests before implementation)

Skip to Task 8, write tests, then return here.

- [ ] **Step 2: Create `CampMapService.cs`**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CampMapService : ICampMapService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IOptions<CampMapOptions> _options;

    public CampMapService(HumansDbContext dbContext, IClock clock, IOptions<CampMapOptions> options)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options;
    }

    public async Task<List<CampPolygonDto>> GetPolygonsAsync(int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampPolygons
            .Include(p => p.CampSeason).ThenInclude(s => s.Camp)
            .Where(p => p.CampSeason.Year == year)
            .Select(p => new CampPolygonDto(
                p.CampSeasonId,
                p.CampSeason.Name,
                p.CampSeason.Camp.Slug,
                p.GeoJson,
                p.AreaSqm))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutPolygonAsync(int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Include(s => s.Camp)
            .Where(s => s.Year == year
                && !_dbContext.CampPolygons.Any(p => p.CampSeasonId == s.Id))
            .Select(s => new CampSeasonSummaryDto(s.Id, s.Name, s.Camp.Slug))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PolygonHistoryEntryDto>> GetPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampPolygonHistories
            .Include(h => h.ModifiedByUser)
            .Where(h => h.CampSeasonId == campSeasonId)
            .OrderByDescending(h => h.ModifiedAt)
            .Select(h => new PolygonHistoryEntryDto(
                h.Id,
                h.ModifiedByUser.UserName ?? h.ModifiedByUserId.ToString(),
                h.ModifiedAt.ToString("g", null),
                h.AreaSqm,
                h.Note))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid?> GetUserCampSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampLeads
            .Where(l => l.UserId == userId && l.LeftAt == null)
            .Join(_dbContext.CampSeasons,
                l => l.CampId,
                s => s.CampId,
                (l, s) => s)
            .Where(s => s.Year == year)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(CampPolygon polygon, CampPolygonHistory history)> SavePolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        var polygon = await _dbContext.CampPolygons
            .FirstOrDefaultAsync(p => p.CampSeasonId == campSeasonId, cancellationToken);

        if (polygon == null)
        {
            polygon = new CampPolygon
            {
                CampSeasonId = campSeasonId,
                GeoJson = geoJson,
                AreaSqm = areaSqm,
                LastModifiedByUserId = modifiedByUserId,
                LastModifiedAt = now
            };
            _dbContext.CampPolygons.Add(polygon);
        }
        else
        {
            polygon.GeoJson = geoJson;
            polygon.AreaSqm = areaSqm;
            polygon.LastModifiedByUserId = modifiedByUserId;
            polygon.LastModifiedAt = now;
        }

        var history = new CampPolygonHistory
        {
            CampSeasonId = campSeasonId,
            GeoJson = geoJson,
            AreaSqm = areaSqm,
            ModifiedByUserId = modifiedByUserId,
            ModifiedAt = now,
            Note = note
        };
        _dbContext.CampPolygonHistories.Add(history);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return (polygon, history);
    }

    public async Task<(CampPolygon polygon, CampPolygonHistory history)> RestorePolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.CampPolygonHistories
            .FirstOrDefaultAsync(h => h.Id == historyId && h.CampSeasonId == campSeasonId, cancellationToken)
            ?? throw new InvalidOperationException($"History entry {historyId} not found for CampSeason {campSeasonId}.");

        var localDt = entry.ModifiedAt.InUtc().LocalDateTime;
        var note = $"Restored from {localDt:yyyy-MM-dd HH:mm} UTC";
        return await SavePolygonAsync(campSeasonId, entry.GeoJson, entry.AreaSqm, restoredByUserId, note, cancellationToken);
    }

    public async Task<bool> IsUserMapAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var campAdminRoleId = await _dbContext.Roles
            .Where(r => r.Name == RoleNames.CampAdmin)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (campAdminRoleId != Guid.Empty
            && await _dbContext.UserRoles.AnyAsync(
                ur => ur.UserId == userId && ur.RoleId == campAdminRoleId, cancellationToken))
            return true;

        var team = await _dbContext.Teams
            .FirstOrDefaultAsync(t => t.Slug == _options.Value.CityPlanningTeamSlug, cancellationToken);
        if (team == null) return false;

        return await _dbContext.TeamMembers
            .AnyAsync(tm => tm.TeamId == team.Id && tm.UserId == userId && tm.LeftAt == null, cancellationToken);
    }

    public async Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        if (await IsUserMapAdminAsync(userId, cancellationToken)) return true;

        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.IsPlacementOpen) return false;

        var campSeason = await _dbContext.CampSeasons
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, cancellationToken);
        if (campSeason == null) return false;

        return await _dbContext.CampLeads
            .AnyAsync(l => l.CampId == campSeason.CampId && l.UserId == userId && l.LeftAt == null, cancellationToken);
    }

    public async Task<CampMapSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var campSettings = await _dbContext.CampSettings
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("CampSettings row not found.");

        var settings = await _dbContext.CampMapSettings
            .FirstOrDefaultAsync(s => s.Year == campSettings.PublicYear, cancellationToken);
        if (settings != null) return settings;

        settings = new CampMapSettings
        {
            Year = campSettings.PublicYear,
            IsPlacementOpen = false,
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.CampMapSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.IsPlacementOpen = true;
        settings.OpenedAt = _clock.GetCurrentInstant();
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.IsPlacementOpen = false;
        settings.ClosedAt = _clock.GetCurrentInstant();
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateLimitZoneAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.LimitZoneGeoJson = geoJson;
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.LimitZoneGeoJson = null;
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default)
    {
        var polygons = await _dbContext.CampPolygons
            .Include(p => p.CampSeason).ThenInclude(s => s.Camp)
            .Where(p => p.CampSeason.Year == year)
            .ToListAsync(cancellationToken);

        var features = polygons.Select(p =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(p.GeoJson);
            var geom = doc.RootElement.TryGetProperty("geometry", out var g) ? g : doc.RootElement;
            return new
            {
                type = "Feature",
                geometry = geom,
                properties = new
                {
                    campName = p.CampSeason.Name,
                    campSlug = p.CampSeason.Camp.Slug,
                    year = p.CampSeason.Year,
                    areaSqm = p.AreaSqm
                }
            };
        }).ToList();

        return System.Text.Json.JsonSerializer.Serialize(
            new { type = "FeatureCollection", features },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 3: Build Infrastructure project**

```bash
dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj
```

Expected: Build succeeded, 0 errors.

---

### Task 8: Service Tests

**Files:**
- Create: `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs`

Test the service against an in-memory EF database. No mocks except `IClock`. Uses `FakeClock` from existing test infrastructure.

- [ ] **Step 1: Write tests**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class CampMapServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampMapService _sut;
    private readonly CampMapOptions _options = new() { CityPlanningTeamSlug = "city-planning" };

    public CampMapServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(dbOptions);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 15, 12, 0, 0));
        _sut = new CampMapService(_dbContext, _clock, Options.Create(_options));
    }

    public void Dispose() => _dbContext.Dispose();

    // --- Helpers ---

    private async Task<(Camp camp, CampSeason season, User user)> SeedCampWithLeadAsync(int year = 2026)
    {
        var user = new User { Id = Guid.NewGuid(), UserName = "lead@test.com", Email = "lead@test.com" };
        _dbContext.Users.Add(user);

        var camp = new Camp { Id = Guid.NewGuid(), Name = "Test Camp", Slug = "test-camp", ContactEmail = "e@test.com" };
        _dbContext.Camps.Add(camp);

        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = year, Name = "Test Camp", Status = CampSeasonStatus.Active };
        _dbContext.CampSeasons.Add(season);

        _dbContext.CampLeads.Add(new CampLead { Id = Guid.NewGuid(), CampId = camp.Id, UserId = user.Id, Role = CampLeadRole.Primary });

        await _dbContext.SaveChangesAsync();
        return (camp, season, user);
    }

    private async Task SeedCampSettingsAsync(int publicYear = 2026)
    {
        _dbContext.CampSettings.Add(new CampSettings { Id = Guid.NewGuid(), PublicYear = publicYear });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<CampMapSettings> SeedMapSettingsAsync(int year = 2026, bool placementOpen = false)
    {
        await SeedCampSettingsAsync(year);
        var settings = new CampMapSettings
        {
            Year = year,
            IsPlacementOpen = placementOpen,
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.CampMapSettings.Add(settings);
        await _dbContext.SaveChangesAsync();
        return settings;
    }

    private async Task<Guid> SeedCampAdminUserAsync()
    {
        var user = new User { Id = Guid.NewGuid(), UserName = "admin@test.com", Email = "admin@test.com" };
        _dbContext.Users.Add(user);

        var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = RoleNames.CampAdmin, NormalizedName = RoleNames.CampAdmin.ToUpperInvariant() };
        _dbContext.Roles.Add(role);
        _dbContext.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });

        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedCityPlanningTeamMemberAsync()
    {
        var user = new User { Id = Guid.NewGuid(), UserName = "planner@test.com", Email = "planner@test.com" };
        _dbContext.Users.Add(user);

        var team = new Team { Id = Guid.NewGuid(), Name = "City Planning", Slug = "city-planning" };
        _dbContext.Teams.Add(team);
        _dbContext.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id });

        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    // --- Tests ---

    [Fact]
    public async Task SavePolygonAsync_FirstSave_CreatesBothPolygonAndHistory()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";

        await _sut.SavePolygonAsync(season.Id, geoJson, 500.0, user.Id);

        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == season.Id);
        var history = await _dbContext.CampPolygonHistories.SingleAsync(h => h.CampSeasonId == season.Id);

        polygon.GeoJson.Should().Be(geoJson);
        polygon.AreaSqm.Should().Be(500.0);
        history.Note.Should().Be("Saved");
        history.ModifiedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SavePolygonAsync_SecondSave_UpdatesPolygonAndAppendsHistory()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        const string geoJson1 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";
        const string geoJson2 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}}""";

        await _sut.SavePolygonAsync(season.Id, geoJson1, 100.0, user.Id);
        await _sut.SavePolygonAsync(season.Id, geoJson2, 200.0, user.Id);

        var polygonCount = await _dbContext.CampPolygons.CountAsync(p => p.CampSeasonId == season.Id);
        var historyCount = await _dbContext.CampPolygonHistories.CountAsync(h => h.CampSeasonId == season.Id);
        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == season.Id);

        polygonCount.Should().Be(1);
        historyCount.Should().Be(2);
        polygon.GeoJson.Should().Be(geoJson2);
        polygon.AreaSqm.Should().Be(200.0);
    }

    [Fact]
    public async Task RestorePolygonVersionAsync_RestoresGeoJsonWithNote()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        const string originalGeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        var (_, historyEntry) = await _sut.SavePolygonAsync(season.Id, originalGeoJson, 100.0, user.Id);
        await _sut.SavePolygonAsync(season.Id, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,0]]]}}""", 999.0, user.Id);

        await _sut.RestorePolygonVersionAsync(season.Id, historyEntry.Id, user.Id);

        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == season.Id);
        var latestHistory = await _dbContext.CampPolygonHistories
            .OrderByDescending(h => h.ModifiedAt).FirstAsync(h => h.CampSeasonId == season.Id);

        polygon.GeoJson.Should().Be(originalGeoJson);
        latestHistory.Note.Should().StartWith("Restored from");
    }

    [Fact]
    public async Task IsUserMapAdminAsync_CampAdminRole_ReturnsTrue()
    {
        var adminId = await SeedCampAdminUserAsync();
        var result = await _sut.IsUserMapAdminAsync(adminId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserMapAdminAsync_CityPlanningTeamMember_ReturnsTrue()
    {
        var plannerId = await SeedCityPlanningTeamMemberAsync();
        var result = await _sut.IsUserMapAdminAsync(plannerId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_CityPlanningTeamMember_AlwaysTrue_EvenWhenPlacementClosed()
    {
        var plannerId = await SeedCityPlanningTeamMemberAsync();
        var (_, season, _) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: false);

        var result = await _sut.CanUserEditAsync(plannerId, season.Id);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_CampAdmin_AlwaysTrue_EvenWhenPlacementClosed()
    {
        var adminId = await SeedCampAdminUserAsync();
        var (_, season, _) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: false);

        var result = await _sut.CanUserEditAsync(adminId, season.Id);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadWithPlacementOpen_ReturnsTrue()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: true);

        var result = await _sut.CanUserEditAsync(user.Id, season.Id);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadWithPlacementClosed_ReturnsFalse()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: false);

        var result = await _sut.CanUserEditAsync(user.Id, season.Id);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadOfDifferentCamp_ReturnsFalse()
    {
        var (_, season, _) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: true);

        // A different user who is NOT a lead on this camp
        var otherUser = new User { Id = Guid.NewGuid(), UserName = "other@test.com", Email = "other@test.com" };
        _dbContext.Users.Add(otherUser);
        await _dbContext.SaveChangesAsync();

        var result = await _sut.CanUserEditAsync(otherUser.Id, season.Id);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetSettingsAsync_CreatesRowIfMissing()
    {
        await SeedCampSettingsAsync(publicYear: 2026);

        var settings = await _sut.GetSettingsAsync();

        settings.Year.Should().Be(2026);
        settings.IsPlacementOpen.Should().BeFalse();
        (await _dbContext.CampMapSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsExistingRow()
    {
        var existing = await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        var result = await _sut.GetSettingsAsync();

        result.Id.Should().Be(existing.Id);
        result.IsPlacementOpen.Should().BeTrue();
        (await _dbContext.CampMapSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OpenPlacementAsync_SetsIsPlacementOpenTrue()
    {
        await SeedMapSettingsAsync(placementOpen: false);
        var adminId = await SeedCampAdminUserAsync();

        await _sut.OpenPlacementAsync(adminId);

        var settings = await _dbContext.CampMapSettings.SingleAsync();
        settings.IsPlacementOpen.Should().BeTrue();
        settings.OpenedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task ClosePlacementAsync_SetsIsPlacementOpenFalse()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var adminId = await SeedCampAdminUserAsync();

        await _sut.ClosePlacementAsync(adminId);

        var settings = await _dbContext.CampMapSettings.SingleAsync();
        settings.IsPlacementOpen.Should().BeFalse();
        settings.ClosedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task GetPolygonsAsync_ReturnsOnlyPolygonsForYear()
    {
        var (_, season2026, user) = await SeedCampWithLeadAsync(year: 2026);
        var (_, season2027, _) = await SeedCampWithLeadAsync(year: 2027);

        await _sut.SavePolygonAsync(season2026.Id, """{"type":"Feature"}""", 100, user.Id);
        await _sut.SavePolygonAsync(season2027.Id, """{"type":"Feature"}""", 200, user.Id);

        var result = await _sut.GetPolygonsAsync(2026);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(season2026.Id);
    }

    [Fact]
    public async Task GetCampSeasonsWithoutPolygonAsync_ExcludesSeasonsWithPolygon()
    {
        var (_, seasonWith, user) = await SeedCampWithLeadAsync(year: 2026);
        var (_, seasonWithout, _) = await SeedCampWithLeadAsync(year: 2026);

        await _sut.SavePolygonAsync(seasonWith.Id, """{"type":"Feature"}""", 100, user.Id);

        var result = await _sut.GetCampSeasonsWithoutPolygonAsync(2026);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(seasonWithout.Id);
    }
}
```

- [ ] **Step 2: Run tests (expect failures — service not registered yet)**

```bash
dotnet test tests/Humans.Application.Tests/ --filter "CampMapServiceTests" -v n
```

Expected: Tests that don't depend on DI registration should PASS; compilation must succeed.

- [ ] **Step 3: Fix any compilation errors, then re-run**

```bash
dotnet test tests/Humans.Application.Tests/ --filter "CampMapServiceTests"
```

Expected: All tests PASS.

---

### Task 9: DI Registration

**Files:**
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

- [ ] **Step 1: Add `CampMapOptions` config and `ICampMapService` registration**

After `services.Configure<TeamResourceManagementSettings>(...)`, add:

```csharp
services.Configure<CampMapOptions>(configuration.GetSection(CampMapOptions.SectionName));
```

After `services.AddScoped<ICampService, CampService>();`, add:

```csharp
services.AddScoped<ICampMapService, CampMapService>();
```

- [ ] **Step 2: Build Web project**

```bash
dotnet build src/Humans.Web/Humans.Web.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit Chunk 2**

```bash
git add src/Humans.Infrastructure/Configuration/CampMapOptions.cs \
        src/Humans.Web/appsettings.json \
        src/Humans.Application/Interfaces/ICampMapService.cs \
        src/Humans.Infrastructure/Services/CampMapService.cs \
        src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs \
        tests/Humans.Application.Tests/Services/CampMapServiceTests.cs
git commit -m "feat: add CampMapService, ICampMapService, and service tests"
```

---

## Chunk 3: Hub + API + Wiring

### Task 10: SignalR Hub

**Files:**
- Create: `src/Humans.Web/Hubs/CampMapHub.cs`

- [ ] **Step 1: Create the Hubs directory and hub class**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Humans.Web.Hubs;

[Authorize]
public class CampMapHub : Hub
{
    /// <summary>
    /// Called by clients to broadcast their cursor position.
    /// Relayed to all other connected clients.
    /// </summary>
    public async Task UpdateCursor(double lat, double lng)
    {
        var userName = Context.User?.Identity?.Name ?? "Unknown";
        await Clients.Others.SendAsync("CursorMoved", Context.ConnectionId, userName, lat, lng);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Clients.Others.SendAsync("CursorLeft", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
```

---

### Task 11: API Controller

**Files:**
- Create: `src/Humans.Web/Controllers/CampMapApiController.cs`

- [ ] **Step 1: Create the API controller**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Humans.Web.Controllers;

[Authorize]
[Route("api/camp-map")]
[ApiController]
public class CampMapApiController : ControllerBase
{
    private readonly ICampMapService _campMapService;
    private readonly IHubContext<CampMapHub> _hubContext;
    private readonly UserManager<User> _userManager;

    public CampMapApiController(
        ICampMapService campMapService,
        IHubContext<CampMapHub> hubContext,
        UserManager<User> userManager)
    {
        _campMapService = campMapService;
        _hubContext = hubContext;
        _userManager = userManager;
    }

    private Guid CurrentUserId() => Guid.Parse(_userManager.GetUserId(User)!);

    /// <summary>Returns current map state: settings, all polygons, unmapped seasons.</summary>
    [HttpGet("state")]
    public async Task<IActionResult> GetState(CancellationToken cancellationToken)
    {
        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var polygons = await _campMapService.GetPolygonsAsync(settings.Year, cancellationToken);
        var seasonsWithout = await _campMapService.GetCampSeasonsWithoutPolygonAsync(settings.Year, cancellationToken);

        return Ok(new
        {
            isPlacementOpen = settings.IsPlacementOpen,
            limitZoneGeoJson = settings.LimitZoneGeoJson,
            polygons,
            seasonsWithoutPolygon = seasonsWithout
        });
    }

    /// <summary>Returns polygon version history for a camp season, newest first.</summary>
    [HttpGet("polygons/{campSeasonId:guid}/history")]
    public async Task<IActionResult> GetHistory(Guid campSeasonId, CancellationToken cancellationToken)
    {
        var history = await _campMapService.GetPolygonHistoryAsync(campSeasonId, cancellationToken);
        return Ok(history);
    }

    /// <summary>Save or update a polygon. Broadcasts update to all connected clients via SignalR.</summary>
    [HttpPut("polygons/{campSeasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePolygon(
        Guid campSeasonId,
        [FromBody] SavePolygonRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.CanUserEditAsync(userId, campSeasonId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _campMapService.SavePolygonAsync(
            campSeasonId, request.GeoJson, request.AreaSqm, userId,
            cancellationToken: cancellationToken);

        await _hubContext.Clients.All.SendAsync(
            "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, cancellationToken);

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Restore a polygon to a historical version. Map admins only.</summary>
    [HttpPost("polygons/{campSeasonId:guid}/restore/{historyId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestorePolygon(
        Guid campSeasonId,
        Guid historyId,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _campMapService.RestorePolygonVersionAsync(
            campSeasonId, historyId, userId, cancellationToken);

        await _hubContext.Clients.All.SendAsync(
            "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, cancellationToken);

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Export all polygons for a year as GeoJSON FeatureCollection. Map admins only.</summary>
    [HttpGet("export.geojson")]
    public async Task<IActionResult> ExportGeoJson([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var exportYear = year ?? settings.Year;
        var geoJson = await _campMapService.ExportAsGeoJsonAsync(exportYear, cancellationToken);

        return Content(geoJson, "application/geo+json");
    }
}
```

---

### Task 12: MVC Page Controller

**Files:**
- Create: `src/Humans.Web/Controllers/BarrioMapController.cs`

- [ ] **Step 1: Create `BarrioMapController.cs`**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[Route("BarrioMap")]
public class BarrioMapController : Controller
{
    private readonly ICampMapService _campMapService;
    private readonly UserManager<User> _userManager;

    public BarrioMapController(ICampMapService campMapService, UserManager<User> userManager)
    {
        _campMapService = campMapService;
        _userManager = userManager;
    }

    private Guid CurrentUserId() => Guid.Parse(_userManager.GetUserId(User)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var isMapAdmin = await _campMapService.IsUserMapAdminAsync(userId, cancellationToken);
        var userSeasonId = await _campMapService.GetUserCampSeasonIdForYearAsync(userId, settings.Year, cancellationToken);
        var seasonsWithout = await _campMapService.GetCampSeasonsWithoutPolygonAsync(settings.Year, cancellationToken);

        ViewBag.IsPlacementOpen = settings.IsPlacementOpen;
        ViewBag.IsMapAdmin = isMapAdmin;
        ViewBag.UserCampSeasonId = userSeasonId?.ToString() ?? string.Empty;
        ViewBag.CurrentUserId = userId.ToString();
        ViewBag.SeasonsWithoutPolygon = seasonsWithout;
        ViewBag.Year = settings.Year;

        return View();
    }

    [HttpGet("Admin")]
    public async Task<IActionResult> Admin(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        ViewBag.Settings = await _campMapService.GetSettingsAsync(cancellationToken);
        return View();
    }

    [HttpPost("Admin/OpenPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenPlacement(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.OpenPlacementAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/ClosePlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePlacement(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.ClosePlacementAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UploadLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLimitZone(IFormFile file, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        await _campMapService.UpdateLimitZoneAsync(geoJson, userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/DeleteLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLimitZone(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.DeleteLimitZoneAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }
}
```

---

### Task 13: Program.cs + CSP + Wiring

**Files:**
- Modify: `src/Humans.Web/Program.cs`
- Modify: `src/Humans.Web/Middleware/CspNonceMiddleware.cs`

- [ ] **Step 1: Add SignalR to `Program.cs`**

After `builder.Services.AddRazorPages();` (around line 259), add:

```csharp
builder.Services.AddSignalR();
```

- [ ] **Step 2: Map the hub in `Program.cs`**

After `app.MapRazorPages();` (around line 424), add:

```csharp
app.MapHub<CampMapHub>("/hubs/camp-map");
```

Also add the using at the top of `Program.cs`:

```csharp
using Humans.Web.Hubs;
```

- [ ] **Step 3: Update CSP in `CspNonceMiddleware.cs`**

Replace the existing `context.Response.Headers.Append("Content-Security-Policy", ...)` call with:

```csharp
context.Response.Headers.Append("Content-Security-Policy",
    $"default-src 'self'; " +
    $"script-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net https://unpkg.com https://maps.googleapis.com https://maps.gstatic.com; " +
    "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://fonts.googleapis.com https://unpkg.com; " +
    "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
    "img-src 'self' https: data:; " +
    "connect-src 'self' https://cdn.jsdelivr.net https://unpkg.com https://maps.googleapis.com https://maps.gstatic.com https://places.googleapis.com https://server.arcgisonline.com wss: ws:; " +
    "worker-src blob:; " +
    "frame-ancestors 'none'");
```

Key additions:
- `https://server.arcgisonline.com` in `connect-src` (ESRI tile requests)
- `wss: ws:` in `connect-src` (SignalR WebSocket)
- `worker-src blob:` (MapLibre GL JS web workers)
- `https://unpkg.com` in `style-src` (maplibre-gl-draw CSS)

- [ ] **Step 4: Build and run to smoke test**

```bash
dotnet build Humans.slnx
dotnet run --project src/Humans.Web
```

Navigate to `https://localhost:5001/BarrioMap` — should redirect to login (auth working). Navigate to `https://localhost:5001/hubs/camp-map` — should return 400 (hub exists, expects WebSocket upgrade).

- [ ] **Step 5: Commit Chunk 3**

```bash
git add src/Humans.Web/Hubs/CampMapHub.cs \
        src/Humans.Web/Controllers/CampMapApiController.cs \
        src/Humans.Web/Controllers/BarrioMapController.cs \
        src/Humans.Web/Program.cs \
        src/Humans.Web/Middleware/CspNonceMiddleware.cs
git commit -m "feat: add CampMapHub, CampMapApiController, BarrioMapController, SignalR wiring"
```

---

## Chunk 4: Views + Frontend

### Task 14: About Page CDN Attribution

**Files:**
- Modify: `src/Humans.Web/Views/Home/About.cshtml`

- [ ] **Step 1: Add CDN entries to the Frontend table (after Font Awesome row)**

```html
<tr>
    <td><a href="https://maplibre.org/maplibre-gl-js/docs/" target="_blank">MapLibre GL JS</a></td>
    <td>4.7.1</td>
    <td>BSD-3-Clause</td>
</tr>
<tr>
    <td><a href="https://github.com/maplibre/maplibre-gl-draw" target="_blank">maplibre-gl-draw</a></td>
    <td>1.4.0</td>
    <td>ISC</td>
</tr>
<tr>
    <td><a href="https://turfjs.org/" target="_blank">Turf.js</a></td>
    <td>7.1.0</td>
    <td>MIT</td>
</tr>
<tr>
    <td><a href="https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client" target="_blank">@microsoft/signalr</a></td>
    <td>8.0.7</td>
    <td>MIT</td>
</tr>
```

- [ ] **Step 2: Add BSD-3-Clause and ISC to the License Compliance Summary**

In the license summary `<ul>`, after the MIT entry, add:

```html
<li><strong>BSD-3-Clause</strong> (MapLibre GL JS) &mdash; requires copyright notice; this page satisfies that obligation.</li>
<li><strong>ISC</strong> (maplibre-gl-draw) &mdash; permissive, functionally equivalent to BSD-2-Clause; copyright notice required.</li>
```

---

### Task 15: Admin View

**Files:**
- Create: `src/Humans.Web/Views/BarrioMap/Admin.cshtml`

- [ ] **Step 1: Create `Admin.cshtml`**

```cshtml
@{
    ViewData["Title"] = "Barrio Map Admin";
    var settings = (Humans.Domain.Entities.CampMapSettings)ViewBag.Settings;
}

<div class="container py-4">
    <div class="d-flex align-items-center gap-3 mb-4">
        <h1 class="mb-0">Barrio Map Admin</h1>
        <a href="/BarrioMap" class="btn btn-outline-secondary btn-sm">
            <i class="fa fa-map"></i> View Map
        </a>
    </div>

    <div class="row g-4">
        <!-- Placement Phase -->
        <div class="col-md-6">
            <div class="card">
                <div class="card-header fw-semibold">
                    <i class="fa fa-door-open me-1"></i> Placement Phase (@settings.Year)
                </div>
                <div class="card-body">
                    <p class="mb-3">
                        Status:
                        @if (settings.IsPlacementOpen)
                        {
                            <span class="badge bg-success fs-6">Open</span>
                            if (settings.OpenedAt.HasValue)
                            {
                                <span class="text-muted ms-2 small">Opened @settings.OpenedAt.Value.ToString("g", null)</span>
                            }
                        }
                        else
                        {
                            <span class="badge bg-secondary fs-6">Closed</span>
                            if (settings.ClosedAt.HasValue)
                            {
                                <span class="text-muted ms-2 small">Closed @settings.ClosedAt.Value.ToString("g", null)</span>
                            }
                        }
                    </p>
                    @if (settings.IsPlacementOpen)
                    {
                        <form method="post" action="/BarrioMap/Admin/ClosePlacement">
                            @Html.AntiForgeryToken()
                            <button type="submit" class="btn btn-warning">
                                <i class="fa fa-lock me-1"></i> Close Placement
                            </button>
                        </form>
                    }
                    else
                    {
                        <form method="post" action="/BarrioMap/Admin/OpenPlacement">
                            @Html.AntiForgeryToken()
                            <button type="submit" class="btn btn-success">
                                <i class="fa fa-lock-open me-1"></i> Open Placement
                            </button>
                        </form>
                    }
                </div>
            </div>
        </div>

        <!-- Limit Zone -->
        <div class="col-md-6">
            <div class="card">
                <div class="card-header fw-semibold">
                    <i class="fa fa-draw-polygon me-1"></i> Site Limit Zone (@settings.Year)
                </div>
                <div class="card-body">
                    @if (settings.LimitZoneGeoJson != null)
                    {
                        <p class="text-success mb-3"><i class="fa fa-check-circle me-1"></i> Limit zone uploaded</p>
                        <form method="post" action="/BarrioMap/Admin/DeleteLimitZone" class="d-inline">
                            @Html.AntiForgeryToken()
                            <button type="submit" class="btn btn-outline-danger btn-sm"
                                    onclick="return confirm('Delete the limit zone?')">
                                <i class="fa fa-trash me-1"></i> Remove
                            </button>
                        </form>
                    }
                    else
                    {
                        <p class="text-muted mb-3">No limit zone uploaded yet.</p>
                    }
                    <form method="post" action="/BarrioMap/Admin/UploadLimitZone" enctype="multipart/form-data" class="mt-3">
                        @Html.AntiForgeryToken()
                        <div class="mb-2">
                            <label class="form-label small fw-semibold">Upload GeoJSON file</label>
                            <input type="file" name="file" accept=".geojson,.json" class="form-control form-control-sm" required />
                        </div>
                        <button type="submit" class="btn btn-primary btn-sm">
                            <i class="fa fa-upload me-1"></i> Upload
                        </button>
                    </form>
                </div>
            </div>
        </div>

        <!-- Export -->
        <div class="col-12">
            <div class="card">
                <div class="card-header fw-semibold">
                    <i class="fa fa-file-export me-1"></i> Export
                </div>
                <div class="card-body">
                    <p class="mb-2">Download all camp polygons for @settings.Year as a GeoJSON FeatureCollection.</p>
                    <a href="/api/camp-map/export.geojson?year=@settings.Year" class="btn btn-outline-secondary btn-sm" download="barrio-map-@settings.Year.geojson">
                        <i class="fa fa-download me-1"></i> Download GeoJSON (@settings.Year)
                    </a>
                </div>
            </div>
        </div>
    </div>
</div>
```

---

### Task 16: Map Index View

**Files:**
- Create: `src/Humans.Web/Views/BarrioMap/Index.cshtml`

This is the main interactive map page. All JS is in a single nonce-protected `<script>` block.

- [ ] **Step 1: Create `Index.cshtml`**

```cshtml
@using System.Text.Json
@{
    ViewData["Title"] = "Barrio Map";
    var nonce = Context.Items["CspNonce"]?.ToString() ?? string.Empty;
    var isPlacementOpen = (bool)ViewBag.IsPlacementOpen;
    var isMapAdmin = (bool)ViewBag.IsMapAdmin;
    var userCampSeasonId = (string)ViewBag.UserCampSeasonId;
    var seasonsWithout = (List<Humans.Application.Interfaces.CampSeasonSummaryDto>)ViewBag.SeasonsWithoutPolygon;
    var year = (int)ViewBag.Year;
}

@section Styles {
    <link rel="stylesheet" href="https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.css" />
    <link rel="stylesheet" href="https://unpkg.com/@@maplibre/maplibre-gl-draw@1.4.0/dist/maplibre-gl-draw.css" />
    <style nonce="@nonce">
        #map { height: calc(100vh - 56px); width: 100%; }
        #map-toolbar {
            position: absolute; top: 70px; left: 10px; z-index: 10;
            display: flex; flex-direction: column; gap: 8px;
        }
        #area-badge {
            position: absolute; bottom: 30px; left: 10px; z-index: 10;
            background: rgba(0,0,0,0.7); color: white;
            padding: 4px 10px; border-radius: 4px; font-size: 13px;
            display: none;
        }
        .remote-cursor {
            background: rgba(255,165,0,0.85); color: white;
            padding: 2px 7px; border-radius: 4px; font-size: 12px;
            white-space: nowrap; pointer-events: none;
        }
    </style>
}

<div style="position:relative;">
    <div id="map"></div>

    <!-- Toolbar -->
    <div id="map-toolbar">
        @if (isPlacementOpen && !string.IsNullOrEmpty(userCampSeasonId))
        {
            <button id="add-my-barrio-btn" class="btn btn-primary btn-sm shadow">
                <i class="fa fa-plus me-1"></i> Add my barrio on the map
            </button>
        }
        @if (isMapAdmin && seasonsWithout.Any())
        {
            <div id="add-barrio-container" class="d-flex gap-2 align-items-center">
                <select id="add-barrio-select" class="form-select form-select-sm shadow" style="width:200px;">
                    <option value="">Add a barrio…</option>
                    @foreach (var s in seasonsWithout)
                    {
                        <option value="@s.CampSeasonId">@s.CampName</option>
                    }
                </select>
            </div>
        }
        <button id="save-btn" class="btn btn-success btn-sm shadow" disabled>
            <i class="fa fa-save me-1"></i> Save
        </button>
        <button id="history-btn" class="btn btn-outline-light btn-sm shadow" disabled>
            <i class="fa fa-history me-1"></i> History
        </button>
        @if (isMapAdmin)
        {
            <a href="/BarrioMap/Admin" class="btn btn-outline-light btn-sm shadow">
                <i class="fa fa-cog me-1"></i> Admin
            </a>
        }
    </div>

    <div id="area-badge"><span id="area-display"></span></div>
</div>

<!-- History offcanvas -->
<div class="offcanvas offcanvas-end" tabindex="-1" id="history-panel" style="width:360px;">
    <div class="offcanvas-header">
        <h5 class="offcanvas-title">Polygon History</h5>
        <button type="button" class="btn-close" data-bs-dismiss="offcanvas"></button>
    </div>
    <div class="offcanvas-body p-2" id="history-list">
        <p class="text-muted text-center py-4">Loading…</p>
    </div>
</div>

<!-- Anti-forgery token for AJAX calls -->
@Html.AntiForgeryToken()

@section Scripts {
    <script nonce="@nonce" src="https://unpkg.com/maplibre-gl@4.7.1/dist/maplibre-gl.js"></script>
    <script nonce="@nonce" src="https://unpkg.com/@@maplibre/maplibre-gl-draw@1.4.0/dist/maplibre-gl-draw.js"></script>
    <script nonce="@nonce" src="https://unpkg.com/@@turf/turf@7.1.0/turf.min.js"></script>
    <script nonce="@nonce" src="https://cdn.jsdelivr.net/npm/@@microsoft/signalr@8.0.7/dist/browser/signalr.min.js"></script>
    <script nonce="@nonce">
        // --- Server-side constants ---
        const USER_CAMP_SEASON_ID = '@userCampSeasonId';
        const IS_PLACEMENT_OPEN = @(isPlacementOpen ? "true" : "false");
        const IS_MAP_ADMIN = @(isMapAdmin ? "true" : "false");

        const ESRI_TILES = 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
        const MAP_CENTER = [-0.13717, 41.69964]; // Nowhere festival site
        const MAP_ZOOM = 17;

        let map, draw, state, connection;
        let activeCampSeasonId = null;
        let remoteCursors = {};

        // --- Initialization ---

        async function init() {
            map = new maplibregl.Map({
                container: 'map',
                style: {
                    version: 8,
                    sources: { esri: { type: 'raster', tiles: [ESRI_TILES], tileSize: 256, attribution: '© Esri' } },
                    layers: [{ id: 'esri-layer', type: 'raster', source: 'esri' }]
                },
                center: MAP_CENTER,
                zoom: MAP_ZOOM
            });

            draw = new MaplibreGLDraw({ displayControlsDefault: false, controls: { trash: true } });
            map.addControl(draw);
            map.addControl(new maplibregl.NavigationControl(), 'top-right');

            map.on('draw.create', onDrawChange);
            map.on('draw.update', onDrawChange);
            map.on('draw.delete', onDrawDelete);

            await new Promise(resolve => map.on('load', resolve));

            state = await (await fetch('/api/camp-map/state')).json();
            renderMap();
            updateAddMyBarrioVisibility();
            initSignalR();
        }

        // --- Map rendering ---

        function renderMap() {
            ['limit-zone-line', 'limit-zone-fill', 'polygons-outline', 'polygons-fill'].forEach(id => {
                if (map.getLayer(id)) map.removeLayer(id);
            });
            ['limit-zone', 'polygons'].forEach(id => {
                if (map.getSource(id)) map.removeSource(id);
            });

            if (state.limitZoneGeoJson) {
                map.addSource('limit-zone', { type: 'geojson', data: JSON.parse(state.limitZoneGeoJson) });
                map.addLayer({ id: 'limit-zone-fill', type: 'fill', source: 'limit-zone', paint: { 'fill-color': '#ffffff', 'fill-opacity': 0.08 } });
                map.addLayer({ id: 'limit-zone-line', type: 'line', source: 'limit-zone', paint: { 'line-color': '#ffffff', 'line-width': 2, 'line-dasharray': [4, 2] } });
            }

            const features = state.polygons.map(p => {
                const f = JSON.parse(p.geoJson);
                f.properties = Object.assign(f.properties || {}, {
                    campSeasonId: p.campSeasonId,
                    campName: p.campName,
                    isOwn: p.campSeasonId === USER_CAMP_SEASON_ID
                });
                return f;
            });

            map.addSource('polygons', { type: 'geojson', data: { type: 'FeatureCollection', features } });
            map.addLayer({
                id: 'polygons-fill', type: 'fill', source: 'polygons',
                paint: {
                    'fill-color': ['case', ['boolean', ['get', 'isOwn'], false], '#00bfff', '#ff6600'],
                    'fill-opacity': 0.35
                }
            });
            map.addLayer({
                id: 'polygons-outline', type: 'line', source: 'polygons',
                paint: {
                    'line-color': ['case', ['boolean', ['get', 'isOwn'], false], '#00bfff', '#ff6600'],
                    'line-width': 2
                }
            });

            map.on('click', 'polygons-fill', onPolygonClick);
            map.on('mouseenter', 'polygons-fill', () => { map.getCanvas().style.cursor = 'pointer'; });
            map.on('mouseleave', 'polygons-fill', () => { map.getCanvas().style.cursor = ''; });
        }

        function onPolygonClick(e) {
            const props = e.features[0].properties;
            const campSeasonId = props.campSeasonId;
            const isOwn = props.campSeasonId === USER_CAMP_SEASON_ID;
            const canEdit = IS_MAP_ADMIN || (IS_PLACEMENT_OPEN && isOwn);

            if (!canEdit) {
                new maplibregl.Popup().setLngLat(e.lngLat)
                    .setHTML(`<strong>${props.campName || 'Camp'}</strong>`)
                    .addTo(map);
                return;
            }

            activeCampSeasonId = campSeasonId;
            draw.deleteAll();

            const poly = state.polygons.find(p => p.campSeasonId === campSeasonId);
            if (poly) {
                const f = JSON.parse(poly.geoJson);
                if (!f.id) f.id = 'active-polygon';
                draw.add(f);
                draw.changeMode('direct_select', { featureId: f.id });
            }

            document.getElementById('history-btn').disabled = false;
            updateSaveButton();
        }

        // --- Toolbar state ---

        function updateAddMyBarrioVisibility() {
            const btn = document.getElementById('add-my-barrio-btn');
            if (!btn) return;
            const hasPolygon = state.polygons.some(p => p.campSeasonId === USER_CAMP_SEASON_ID);
            btn.style.display = (IS_PLACEMENT_OPEN && USER_CAMP_SEASON_ID && !hasPolygon) ? '' : 'none';
        }

        function updateSaveButton() {
            const features = draw.getAll().features;
            document.getElementById('save-btn').disabled = !(features.length > 0 && activeCampSeasonId);
            const areaBadge = document.getElementById('area-badge');
            if (features.length > 0) {
                const area = turf.area(features[0]);
                document.getElementById('area-display').textContent = Math.round(area).toLocaleString() + ' m²';
                areaBadge.style.display = '';
            } else {
                areaBadge.style.display = 'none';
            }
        }

        function onDrawChange() { updateSaveButton(); }
        function onDrawDelete() {
            activeCampSeasonId = null;
            document.getElementById('save-btn').disabled = true;
            document.getElementById('history-btn').disabled = true;
            document.getElementById('area-badge').style.display = 'none';
        }

        // --- Button handlers ---

        document.getElementById('add-my-barrio-btn')?.addEventListener('click', () => {
            activeCampSeasonId = USER_CAMP_SEASON_ID;
            draw.deleteAll();
            draw.changeMode('draw_polygon');
        });

        document.getElementById('add-barrio-select')?.addEventListener('change', e => {
            const val = e.target.value;
            if (!val) return;
            activeCampSeasonId = val;
            draw.deleteAll();
            draw.changeMode('draw_polygon');
            e.target.value = '';
        });

        document.getElementById('save-btn').addEventListener('click', async () => {
            if (!activeCampSeasonId) return;
            const features = draw.getAll().features;
            if (!features.length) return;

            const feature = features[0];
            const areaSqm = turf.area(feature);
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

            const resp = await fetch(`/api/camp-map/polygons/${activeCampSeasonId}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify({ geoJson: JSON.stringify(feature), areaSqm })
            });

            if (resp.ok) {
                draw.deleteAll();
                activeCampSeasonId = null;
                document.getElementById('save-btn').disabled = true;
                document.getElementById('history-btn').disabled = true;
                document.getElementById('area-badge').style.display = 'none';
                // SignalR PolygonUpdated will refresh the map layer
            } else {
                alert('Failed to save polygon. Please try again.');
            }
        });

        document.getElementById('history-btn').addEventListener('click', async () => {
            if (!activeCampSeasonId) return;
            const resp = await fetch(`/api/camp-map/polygons/${activeCampSeasonId}/history`);
            const history = await resp.json();

            const list = document.getElementById('history-list');
            if (!history.length) {
                list.innerHTML = '<p class="text-muted text-center py-4">No history yet.</p>';
            } else {
                list.innerHTML = history.map(h => `
                    <div class="border-bottom py-2 px-1">
                        <div class="d-flex justify-content-between align-items-start">
                            <div>
                                <div class="fw-semibold small">${h.modifiedByDisplayName}</div>
                                <div class="text-muted" style="font-size:12px">${h.modifiedAt} &middot; ${Math.round(h.areaSqm).toLocaleString()} m²</div>
                                <div class="text-secondary" style="font-size:12px">${h.note}</div>
                            </div>
                            <div class="d-flex gap-1 flex-shrink-0">
                                <button class="btn btn-outline-secondary btn-sm py-0 preview-btn" data-id="${h.id}" data-geojson="${encodeURIComponent(h.geoJson)}">Preview</button>
                                ${IS_MAP_ADMIN ? `<button class="btn btn-outline-warning btn-sm py-0 restore-btn" data-id="${h.id}">Restore</button>` : ''}
                            </div>
                        </div>
                    </div>
                `).join('');

                list.querySelectorAll('.preview-btn').forEach(btn => {
                    btn.addEventListener('click', () => {
                        draw.deleteAll();
                        draw.add(JSON.parse(decodeURIComponent(btn.dataset.geojson)));
                    });
                });
                list.querySelectorAll('.restore-btn').forEach(btn => {
                    btn.addEventListener('click', () => restoreVersion(btn.dataset.id));
                });
            }

            bootstrap.Offcanvas.getOrCreateInstance(document.getElementById('history-panel')).show();
        });

        async function restoreVersion(historyId) {
            if (!activeCampSeasonId) return;
            if (!confirm('Restore this polygon version? The current version will be saved to history first.')) return;
            const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
            const resp = await fetch(`/api/camp-map/polygons/${activeCampSeasonId}/restore/${historyId}`, {
                method: 'POST',
                headers: { 'RequestVerificationToken': token }
            });
            if (resp.ok) {
                bootstrap.Offcanvas.getInstance(document.getElementById('history-panel'))?.hide();
                draw.deleteAll();
                activeCampSeasonId = null;
            } else {
                alert('Restore failed.');
            }
        }

        // --- SignalR ---

        function initSignalR() {
            connection = new signalR.HubConnectionBuilder()
                .withUrl('/hubs/camp-map')
                .withAutomaticReconnect()
                .build();

            connection.on('PolygonUpdated', (campSeasonId, geoJson, areaSqm) => {
                const idx = state.polygons.findIndex(p => p.campSeasonId === campSeasonId);
                if (idx >= 0) {
                    state.polygons[idx].geoJson = geoJson;
                    state.polygons[idx].areaSqm = areaSqm;
                } else {
                    state.polygons.push({ campSeasonId, geoJson, areaSqm, campName: '', campSlug: '' });
                }
                // Refresh the GeoJSON source
                const src = map.getSource('polygons');
                if (src) {
                    const features = state.polygons.map(p => {
                        const f = JSON.parse(p.geoJson);
                        f.properties = Object.assign(f.properties || {}, {
                            campSeasonId: p.campSeasonId,
                            campName: p.campName,
                            isOwn: p.campSeasonId === USER_CAMP_SEASON_ID
                        });
                        return f;
                    });
                    src.setData({ type: 'FeatureCollection', features });
                }
                updateAddMyBarrioVisibility();
            });

            connection.on('CursorMoved', (connectionId, userName, lat, lng) => {
                if (!remoteCursors[connectionId]) {
                    const el = document.createElement('div');
                    el.className = 'remote-cursor';
                    el.textContent = userName;
                    remoteCursors[connectionId] = new maplibregl.Marker({ element: el })
                        .setLngLat([lng, lat]).addTo(map);
                } else {
                    remoteCursors[connectionId].setLngLat([lng, lat]);
                }
            });

            connection.on('CursorLeft', connectionId => {
                remoteCursors[connectionId]?.remove();
                delete remoteCursors[connectionId];
            });

            connection.start().catch(console.error);

            map.on('mousemove', e => {
                if (connection.state === signalR.HubConnectionState.Connected) {
                    connection.invoke('UpdateCursor', e.lngLat.lat, e.lngLat.lng).catch(() => {});
                }
            });
        }

        init();
    </script>
}
```

- [ ] **Step 2: Build and smoke test**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
dotnet run --project src/Humans.Web
```

Navigate to `/BarrioMap` — map should load with satellite tiles. Open two browser tabs, move the mouse — cursors from other tab should appear. Draw a polygon and Save — it should appear on both tabs.

Navigate to `/BarrioMap/Admin` — placement status + limit zone upload should render.

- [ ] **Step 4: Commit Chunk 4**

```bash
git add src/Humans.Web/Views/BarrioMap/ \
        src/Humans.Web/Views/Home/About.cshtml
git commit -m "feat: add BarrioMap views — interactive map, admin panel, history offcanvas"
```

---

## Final Verification

- [ ] Run full test suite: `dotnet test Humans.slnx`
- [ ] Check the About page at `/Home/About` — MapLibre, Turf, SignalR, maplibre-gl-draw rows are present
- [ ] As a barrio lead: "Add my barrio on the map" button appears when placement open + no polygon; disappears after placing
- [ ] As a barrio lead: click existing polygon to edit; click Save; changes reflect live on other tabs
- [ ] As a map admin: "Add a barrio" dropdown shows unmapped seasons
- [ ] As a map admin: Admin panel shows placement status + open/close works
- [ ] As a map admin: GeoJSON export at `/api/camp-map/export.geojson` downloads valid FeatureCollection
- [ ] As a regular user: cannot access `/BarrioMap/Admin` (403)
- [ ] Placement closed + regular lead: cannot save polygon (API returns 403)
- [ ] Push to QA: `git push origin main && ./deploy-qa.sh --no-pull`
