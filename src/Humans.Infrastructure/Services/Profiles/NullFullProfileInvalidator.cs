using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// No-op <see cref="IFullProfileInvalidator"/> for test contexts that wire
/// the base <see cref="ProfileService"/> without the caching decorator.
/// </summary>
public sealed class NullFullProfileInvalidator : IFullProfileInvalidator
{
    public Task InvalidateAsync(Guid userId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
