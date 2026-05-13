using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Humans.Infrastructure.Services.Users;

namespace Humans.Infrastructure.HostedServices;

/// <summary>
/// Issue #703. Populates the <see cref="CachingUserService"/> dict once at
/// application startup so reads after deploy hit the cache immediately rather
/// than filling in lazily per user.
/// </summary>
/// <remarks>
/// Non-fatal: if warmup fails the error is logged and the host continues to
/// start. The first user-triggered read will lazily populate entries via
/// <see cref="CachingUserService.GetUserInfoAsync"/>. Warmup is an
/// optimization, not a correctness requirement.
/// </remarks>
public sealed class UserInfoWarmupHostedService : IHostedService
{
    private readonly CachingUserService _cache;
    private readonly ILogger<UserInfoWarmupHostedService> _logger;

    public UserInfoWarmupHostedService(
        CachingUserService cache,
        ILogger<UserInfoWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming UserInfo cache at startup");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _cache.WarmAllAsync(cancellationToken);
            stopwatch.Stop();
            _logger.LogInformation(
                "UserInfo cache warmed in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("UserInfo cache warmup canceled during startup");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to warm UserInfo cache at startup; lazy reads will populate on demand");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
