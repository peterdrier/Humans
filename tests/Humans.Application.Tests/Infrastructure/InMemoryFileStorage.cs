using System.Collections.Concurrent;
using Humans.Application.Interfaces;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IFileStorage"/> for tests. Thread-safe via
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>; exposes the backing
/// store via <see cref="Files"/> for assertions.
/// </summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Files[key] = ms.ToArray();
    }

    public Task SaveAsync(string key, byte[] content, CancellationToken ct = default)
    {
        Files[key] = (byte[])content.Clone();
        return Task.CompletedTask;
    }

    public Task<byte[]?> TryReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Files.TryGetValue(key, out var bytes) ? bytes : null);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
