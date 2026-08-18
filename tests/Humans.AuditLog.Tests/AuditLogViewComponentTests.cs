using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.ViewComponents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.AuditLog.Tests;

/// <summary>
/// Covers <see cref="AuditLogViewComponent"/>'s predicate routing, the <c>since</c> filter and
/// the layout/column selection: the four consumers that used to read audit and hand-roll a
/// table now emit <c>&lt;vc:audit-log&gt;</c> and rely on these.
/// </summary>
public class AuditLogViewComponentTests
{
    private static readonly Instant Noon = Instant.FromUtc(2026, 3, 1, 12, 0);

    private readonly IAuditViewerService _viewer = Substitute.For<IAuditViewerService>();

    private AuditLogViewComponent BuildSut() =>
        new(_viewer, NullLogger<AuditLogViewComponent>.Instance);

    private static AuditEvent Event(Instant occurredAt, string description = "entry") => new(
        Id: Guid.NewGuid(), OccurredAt: occurredAt, Action: AuditAction.StoreProductPriceChanged,
        ActorUserId: null, ActorDisplayName: null, EntityType: "StoreProduct", EntityId: Guid.NewGuid(),
        SubjectUserId: null, SubjectDisplayName: null, TargetTeamId: null, TargetTeamName: null,
        TargetTeamSlug: null, RelatedEntityId: null, RelatedEntityType: null, Description: description,
        Role: null, UserEmail: null, Success: null, ErrorMessage: null, SyncSource: null,
        ResourceId: null, ResourceName: null);

    private static (string? ViewName, AuditLogComponentViewModel Model) Unwrap(IViewComponentResult result)
    {
        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        return (view.ViewName, view.ViewData!.Model.Should().BeOfType<AuditLogComponentViewModel>().Subject);
    }

    [HumansFact]
    public async Task Since_drops_events_recorded_before_the_cutoff()
    {
        var kept = Event(Noon.Plus(Duration.FromDays(2)), "after the cutoff");
        _viewer.GetFilteredAsync("StoreProduct", Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event(Noon.Minus(Duration.FromDays(5)), "before the cutoff"), kept]);

        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            entityType: "StoreProduct", entityId: Guid.NewGuid(), since: Noon));

        model.Events.Should().ContainSingle().Which.Description.Should().Be("after the cutoff");
    }

    [HumansFact]
    public async Task EntityIds_merges_one_query_per_id_newest_first_and_honours_the_limit()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Is<Guid?>(g => g == first), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event(Noon, "oldest")]);
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Is<Guid?>(g => g == second), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event(Noon.Plus(Duration.FromHours(1)), "newest")]);

        // The duplicate id must not produce a duplicate query.
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            entityType: "StoreProduct", entityIds: [first, second, first], limit: 2));

        model.Events.Select(e => e.Description).Should().Equal("newest", "oldest");
        await _viewer.Received(1).GetFilteredAsync(Arg.Any<string?>(), Arg.Is<Guid?>(g => g == first), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task An_empty_EntityIds_list_means_no_matches_not_an_unscoped_query()
    {
        // A zero-line Store order supplies an empty list. Falling through to GetFilteredAsync
        // with a null entityId would show every product's price changes on that order.
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            entityType: "StoreProduct", entityIds: [], actions: "StoreProductPriceChanged"));

        model.Events.Should().BeEmpty();
        await _viewer.DidNotReceive().GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResourceId_routes_to_the_resource_predicate()
    {
        var resourceId = Guid.NewGuid();
        _viewer.GetForResourceAsync(resourceId, Arg.Any<CancellationToken>())
            .Returns([Event(Noon, "resource row")]);

        var (viewName, model) = Unwrap(await BuildSut().InvokeAsync(resourceId: resourceId, layout: "sync"));

        viewName.Should().Be("Sync");
        model.Events.Should().ContainSingle().Which.Description.Should().Be("resource row");
        await _viewer.DidNotReceive().GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GoogleSyncOnly_routes_the_user_predicate_to_the_sync_query()
    {
        var userId = Guid.NewGuid();
        _viewer.GetGoogleSyncForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([Event(Noon, "sync row")]);

        var (_, model) = Unwrap(await BuildSut().InvokeAsync(userId: userId, googleSyncOnly: true));

        model.Events.Should().ContainSingle().Which.Description.Should().Be("sync row");
    }

    [HumansTheory]
    [InlineData(null, "Default")]
    [InlineData("line", "Default")]
    [InlineData("table", "Table")]
    [InlineData("sync", "Sync")]
    [InlineData("nonsense", "Default")]
    public async Task Layout_selects_the_view(string? layout, string expected)
    {
        var result = await BuildSut().InvokeAsync(layout: layout ?? "line");

        Unwrap(result).ViewName.Should().Be(expected);
    }

    [HumansFact]
    public async Task Columns_keeps_canonical_order_and_ignores_unknown_names()
    {
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            layout: "table", columns: "description, WHEN , sideways"));

        model.Columns.Should().Equal("When", "Description");
    }

    [HumansFact]
    public async Task Columns_omitted_renders_all_six()
    {
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(layout: "table"));

        model.Columns.Should().Equal("When", "Actor", "Action", "Subject", "Description", "Target");
    }

    [HumansFact]
    public async Task ColumnLabels_override_headers_by_key_not_by_position()
    {
        // Keys deliberately out of order against `columns`: pairing positionally would
        // mis-header the table, which is the whole reason this is keyed.
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            layout: "table", columns: "when,actor,description",
            columnLabels: "DESCRIPTION: Vista previa , when:Fecha"));

        model.Header("When").Should().Be("Fecha");
        model.Header("Description").Should().Be("Vista previa");
        model.Header("Actor").Should().Be("Actor", "an unsupplied key keeps the English default");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sideways:Nope")]
    [InlineData("when")]
    [InlineData(":Fecha")]
    [InlineData("when:")]
    public async Task ColumnLabels_ignores_junk_and_falls_back_to_the_column_name(string? columnLabels)
    {
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(layout: "table", columnLabels: columnLabels));

        model.Header("When").Should().Be("When");
    }

    [HumansFact]
    public async Task A_viewer_failure_renders_the_empty_state_rather_than_throwing()
    {
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, model) = Unwrap(await BuildSut().InvokeAsync(userId: Guid.NewGuid()));

        model.Events.Should().BeEmpty();
    }
}
