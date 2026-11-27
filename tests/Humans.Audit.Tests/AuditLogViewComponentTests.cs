using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Audit.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Audit.Tests;

public class AuditLogViewComponentTests
{
    private static readonly Instant Noon = Instant.FromUtc(2026, 3, 1, 12, 0);

    private readonly IAuditViewerService _viewer = Substitute.For<IAuditViewerService>();

    private AuditLogViewComponent BuildSut(CancellationToken requestAborted = default)
    {
        var component = new AuditLogViewComponent(_viewer, NullLogger<AuditLogViewComponent>.Instance);
        // Minimal ViewComponentContext so HttpContext.RequestAborted resolves.
        var viewContext = new ViewContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = requestAborted },
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
        component.ViewComponentContext = new ViewComponentContext { ViewContext = viewContext };
        return component;
    }

    private static AuditEntry Entry(Instant occurredAt, string description = "entry") => new(
        Id: Guid.NewGuid(), OccurredAt: occurredAt, Action: TestActions.StoreProductPriceChanged,
        ActorUserId: null, ActorDisplayName: null, SubjectType: "StoreProduct", SubjectId: Guid.NewGuid(),
        PersonUserId: null, PersonDisplayName: null, TeamId: null, TeamName: null,
        TeamSlug: null, TargetId: null, TargetType: null, Description: description,
        Kept: AuditKept.Forever);

    private static (string? ViewName, AuditLogComponentViewModel Model) Unwrap(IViewComponentResult result)
    {
        var view = result.Should().BeOfType<ViewViewComponentResult>().Subject;
        return (view.ViewName, view.ViewData!.Model.Should().BeOfType<AuditLogComponentViewModel>().Subject);
    }

    [HumansFact]
    public async Task Since_EntriesBeforeCutoff_AreDropped()
    {
        var kept = Entry(Noon.Plus(Duration.FromDays(2)), "after the cutoff");
        _viewer.GetFilteredAsync("StoreProduct", Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([Entry(Noon.Minus(Duration.FromDays(5)), "before the cutoff"), kept]);

        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            subjectType: "StoreProduct", subjectId: Guid.NewGuid(), since: Noon));

        model.Entries.Should().ContainSingle().Which.Description.Should().Be("after the cutoff");
    }

    [HumansFact]
    public async Task SubjectIds_SeveralIds_MergeNewestFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Is<Guid?>(g => g == first), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([Entry(Noon, "oldest")]);
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Is<Guid?>(g => g == second), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([Entry(Noon.Plus(Duration.FromHours(1)), "newest")]);

        // The duplicate id must not produce a duplicate query.
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            subjectType: "StoreProduct", subjectIds: [first, second, first], limit: 2, includeTargets: true));

        model.Entries.Select(e => e.Description).Should().Equal("newest", "oldest");
        await _viewer.Received(1).GetFilteredAsync(Arg.Any<string?>(), Arg.Is<Guid?>(g => g == first), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Invoke_EmptySubjectIds_MatchesNothing()
    {
        // A zero-line Store order supplies an empty list. Falling through to GetFilteredAsync
        // with a null subjectId would show every product's price changes on that order.
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            subjectType: "StoreProduct", subjectIds: [], actions: "StoreProductPriceChanged"));

        model.Entries.Should().BeEmpty();
        await _viewer.DidNotReceive().GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(null, "Default")]
    [InlineData("line", "Default")]
    [InlineData("table", "Table")]
    [InlineData("nonsense", "Default")]
    public async Task Layout_SelectsView(string? layout, string expected)
    {
        var result = await BuildSut().InvokeAsync(layout: layout ?? "line");

        Unwrap(result).ViewName.Should().Be(expected);
    }

    [HumansFact]
    public async Task Columns_UnknownNames_AreIgnored()
    {
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(
            layout: "table", columns: "description, WHEN , sideways"));

        model.Columns.Should().Equal("When", "Description");
    }

    [HumansFact]
    public async Task Columns_Omitted_RendersAllSix()
    {
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(layout: "table"));

        model.Columns.Should().Equal("When", "Actor", "Action", "Person", "Description", "Team");
    }

    [HumansFact]
    public async Task ColumnLabels_OverrideHeadersByKey()
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
    public async Task ColumnLabels_Junk_UsesColumnName(string? columnLabels)
    {
        var (_, model) = Unwrap(await BuildSut().InvokeAsync(layout: "table", columnLabels: columnLabels));

        model.Header("When").Should().Be("When");
    }

    [HumansFact]
    public async Task Invoke_ViewerFails_RendersEmptyState()
    {
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var (_, model) = Unwrap(await BuildSut().InvokeAsync(userId: Guid.NewGuid()));

        model.Entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Invoke_RequestCancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _viewer.GetFilteredAsync(Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var act = () => BuildSut(cancellation.Token).InvokeAsync(userId: Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
