using System.Collections.Concurrent;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="IProfilePictureStore"/> test double. Records calls so
/// tests can assert dual-write, read-through, and delete behaviour without
/// touching the filesystem.
/// </summary>
internal sealed class InMemoryProfilePictureStore : IProfilePictureStore
{
    public ConcurrentDictionary<Guid, (byte[] Data, string ContentType)> Files { get; } = new();

    public Task<(byte[] Data, string ContentType)?> TryReadAsync(
        Guid profileId, CancellationToken ct = default)
    {
        if (Files.TryGetValue(profileId, out var entry))
        {
            return Task.FromResult<(byte[] Data, string ContentType)?>(entry);
        }
        return Task.FromResult<(byte[] Data, string ContentType)?>(null);
    }

    public Task WriteAsync(
        Guid profileId, byte[] data, string contentType, CancellationToken ct = default)
    {
        Files[profileId] = (data, contentType);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid profileId, CancellationToken ct = default)
    {
        Files.TryRemove(profileId, out _);
        return Task.CompletedTask;
    }
}
