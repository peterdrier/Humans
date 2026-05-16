namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Thrown by a <see cref="TrackedCache{TKey,TValue}"/>-backed Singleton when a
/// load-all read is attempted before startup warmup has completed (or after a
/// bulk invalidation that the cache does not re-warm on demand). The contract
/// for load-all reads is "all rows or this exception" — never a partial set.
///
/// <para>Should be rare in practice: the cache warms during
/// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/>, which
/// completes before the host accepts requests. Surfacing this exception
/// indicates either warmup failed (logged at error) or a caller is reading
/// during startup before the host is ready.</para>
/// </summary>
public sealed class CantLoadAllException : Exception
{
    public CantLoadAllException()
    {
    }

    public CantLoadAllException(string message)
        : base(message)
    {
    }

    public CantLoadAllException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public static CantLoadAllException ForCache(string cacheName) =>
        new($"Cache '{cacheName}' is not warmed — load-all read refused. " +
            $"This indicates startup warmup failed or a read raced the host.")
        {
            CacheName = cacheName,
        };

    public string? CacheName { get; private init; }
}
