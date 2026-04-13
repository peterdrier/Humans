using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Authorization;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class SyncSettingsService : ISyncSettingsService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuthorizationService _authorizationService;
    private readonly IClock _clock;
    private readonly ILogger<SyncSettingsService> _logger;

    public SyncSettingsService(
        HumansDbContext dbContext,
        IAuthorizationService authorizationService,
        IClock clock,
        ILogger<SyncSettingsService> logger)
    {
        _dbContext = dbContext;
        _authorizationService = authorizationService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.SyncServiceSettings
            .Include(s => s.UpdatedByUser)
            .OrderBy(s => s.ServiceType)
            .ToListAsync(ct);
    }

    public async Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
    {
        var setting = await _dbContext.SyncServiceSettings
            .FirstOrDefaultAsync(s => s.ServiceType == serviceType, ct);
        return setting?.SyncMode ?? SyncMode.None;
    }

    public async Task UpdateModeAsync(
        SyncServiceType serviceType,
        SyncMode mode,
        Guid actorUserId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        // Guard: changing sync settings has downstream external side effects
        // (it governs whether scheduled jobs call Google APIs). Enforce authorization
        // at the service boundary so controllers can't be the only line of defense.
        var authResult = await _authorizationService.AuthorizeAsync(
            principal,
            nameof(UpdateModeAsync),
            GoogleSyncOperationRequirement.Execute);

        if (!authResult.Succeeded)
        {
            _logger.LogWarning(
                "Authorization denied for sync settings update: principal {Principal} attempted to set {ServiceType} to {Mode}",
                principal.Identity?.Name, serviceType, mode);
            throw new UnauthorizedAccessException(
                $"Principal is not authorized to update sync settings for {serviceType}.");
        }

        var setting = await _dbContext.SyncServiceSettings
            .FirstOrDefaultAsync(s => s.ServiceType == serviceType, ct)
            ?? throw new InvalidOperationException($"No sync setting found for {serviceType}");

        setting.SyncMode = mode;
        setting.UpdatedAt = _clock.GetCurrentInstant();
        setting.UpdatedByUserId = actorUserId;
        await _dbContext.SaveChangesAsync(ct);
    }
}
