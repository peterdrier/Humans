using AwesomeAssertions;
using Humans.Domain.Enums;
using Humans.Infrastructure.Stores;
using NodaTime;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Tests.Stores;

public sealed class ApplicationStoreTests
{
    private readonly ApplicationStore _store = new();

    [Fact]
    public void Upsert_AddsNewApplication_GetByIdReturnsIt()
    {
        var app = NewApp();

        _store.Upsert(app);

        _store.GetById(app.Id).Should().BeSameAs(app);
    }

    [Fact]
    public void Upsert_ReplacesExistingApplication()
    {
        var id = Guid.NewGuid();
        _store.Upsert(NewApp(id));
        var replacement = NewApp(id);

        _store.Upsert(replacement);

        _store.GetById(id).Should().BeSameAs(replacement);
    }

    [Fact]
    public void Remove_DeletesApplication()
    {
        var app = NewApp();
        _store.Upsert(app);

        _store.Remove(app.Id);

        _store.GetById(app.Id).Should().BeNull();
    }

    [Fact]
    public void Remove_Unknown_IsNoOp()
    {
        var act = () => _store.Remove(Guid.NewGuid());
        act.Should().NotThrow();
    }

    [Fact]
    public void GetById_Unknown_ReturnsNull()
    {
        _store.GetById(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void GetByUserId_ReturnsApplicationsForUserOrderedBySubmittedAtDesc()
    {
        var userId = Guid.NewGuid();
        var older = NewApp(userId: userId, submittedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
        var newer = NewApp(userId: userId, submittedAt: Instant.FromUtc(2026, 3, 1, 0, 0));
        _store.Upsert(older);
        _store.Upsert(newer);

        var result = _store.GetByUserId(userId);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(newer.Id);
        result[1].Id.Should().Be(older.Id);
    }

    [Fact]
    public void GetByUserId_ExcludesOtherUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _store.Upsert(NewApp(userId: userA));
        _store.Upsert(NewApp(userId: userB));

        var result = _store.GetByUserId(userA);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userA);
    }

    [Fact]
    public void CountSubmitted_CountsOnlySubmittedStatus()
    {
        _store.Upsert(NewApp());
        _store.Upsert(NewApp());

        _store.CountSubmitted().Should().Be(2);
    }

    [Fact]
    public void GetAll_ReturnsSnapshot()
    {
        _store.Upsert(NewApp());
        _store.Upsert(NewApp());
        _store.Upsert(NewApp());

        _store.GetAll().Should().HaveCount(3);
    }

    [Fact]
    public void LoadAll_ReplacesContents()
    {
        _store.Upsert(NewApp());
        var replacement = new[] { NewApp(), NewApp(), NewApp() };

        _store.LoadAll(replacement);

        _store.GetAll().Should().HaveCount(3);
    }

    [Fact]
    public void Upsert_ParallelInvocations_AllSucceed()
    {
        var apps = Enumerable.Range(0, 100).Select(_ => NewApp()).ToList();

        Parallel.ForEach(apps, app => _store.Upsert(app));

        _store.GetAll().Should().HaveCount(100);
    }

    private static MemberApplication NewApp(
        Guid? id = null,
        Guid? userId = null,
        Instant? submittedAt = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        UserId = userId ?? Guid.NewGuid(),
        MembershipTier = MembershipTier.Colaborador,
        Motivation = "m",
        SubmittedAt = submittedAt ?? Instant.FromUtc(2026, 3, 1, 12, 0),
        UpdatedAt = submittedAt ?? Instant.FromUtc(2026, 3, 1, 12, 0)
    };
}
