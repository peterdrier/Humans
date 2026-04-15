using System.Collections.Concurrent;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Stores;

/// <summary>
/// Dictionary-backed canonical store for <see cref="MemberApplication"/>.
/// Registered as a DI singleton; warmed on startup via
/// <c>ApplicationStoreWarmupHostedService</c>.
/// </summary>
public sealed class ApplicationStore : IApplicationStore
{
    private readonly ConcurrentDictionary<Guid, MemberApplication> _byId = new();

    public MemberApplication? GetById(Guid applicationId) =>
        _byId.TryGetValue(applicationId, out var app) ? app : null;

    public IReadOnlyList<MemberApplication> GetByUserId(Guid userId) =>
        _byId.Values
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToList();

    public IReadOnlyList<MemberApplication> GetAll() => _byId.Values.ToList();

    public int CountSubmitted() =>
        _byId.Values.Count(a => a.Status == ApplicationStatus.Submitted);

    public void Upsert(MemberApplication application) =>
        _byId[application.Id] = application;

    public void Remove(Guid applicationId) => _byId.TryRemove(applicationId, out _);

    public void LoadAll(IReadOnlyList<MemberApplication> applications)
    {
        _byId.Clear();
        foreach (var app in applications)
        {
            _byId[app.Id] = app;
        }
    }
}
