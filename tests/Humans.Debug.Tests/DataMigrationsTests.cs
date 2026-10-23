using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Debug.Controllers;
using Humans.Debug.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Humans.Debug.Tests;

/// <summary>
/// The data-migrations host: <c>/Debug/DataMigrations</c> lists and applies every section's
/// migrations (admin-only, audited, idempotent), and <c>/api/data-migrations</c> publishes their
/// progress anonymously — counts only, never row data.
/// </summary>
public class DataMigrationsTests
{
    private const string Key = "fake.copy";
    private const string PendingEmail = "pending-member@example.org";

    private readonly List<string> _pending = [PendingEmail, "second@example.org"];
    private readonly FakeMigrations _migrations;
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IServiceProvider _services;
    private readonly Guid _actorId = Guid.NewGuid();

    public DataMigrationsTests()
    {
        _migrations = new FakeMigrations(_pending);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_audit);
        services.AddSingleton<ISectionDataMigrations>(_migrations);
        services.AddScoped<DataMigrationService>();
        _services = services.BuildServiceProvider().CreateScope().ServiceProvider;
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private DataMigrationService Service => _services.GetRequiredService<DataMigrationService>();

    [HumansFact]
    public async Task Reports_carry_the_pending_count_and_every_pending_row()
    {
        var report = (await Service.GetReportsAsync(Ct)).Should().ContainSingle().Subject;

        report.Migration.Key.Should().Be(Key);
        report.Status.Pending.Should().Be(2);
        report.Status.Done.Should().BeFalse();
        report.Status.PendingRows.Select(r => r[0]).Should().Equal(PendingEmail, "second@example.org");
    }

    [HumansFact]
    public async Task Apply_runs_the_owner_delegate_for_the_actor_and_audits_key_actor_and_rows()
    {
        var rows = await Service.ApplyAsync(Key, _actorId, Ct);

        rows.Should().Be(2);
        _migrations.LastActor.Should().Be(_actorId);
        await _audit.Received(1).LogAsync(
            AuditAction.DataMigrationApplied, "DataMigration", Guid.Empty,
            Arg.Is<string>(s => s.Contains(Key) && s.Contains("2 row(s)")),
            _actorId);
    }

    [HumansFact]
    public async Task A_second_apply_finds_nothing_pending()
    {
        await Service.ApplyAsync(Key, _actorId, Ct);

        (await Service.ApplyAsync(Key, _actorId, Ct)).Should().Be(0);
        (await Service.GetReportsAsync(Ct)).Single().Status.Done.Should().BeTrue();
    }

    [HumansFact]
    public async Task Apply_of_an_unknown_key_runs_nothing_and_audits_nothing()
    {
        (await Service.ApplyAsync("nope.missing", _actorId, Ct)).Should().BeNull();

        _migrations.ApplyCalls.Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public void Screen_is_admin_only_and_apply_is_an_antiforgery_post()
    {
        typeof(DataMigrationsController).GetCustomAttribute<AuthorizeAttribute>(inherit: false)!.Policy
            .Should().Be(PolicyNames.AdminOnly);
        typeof(DataMigrationsController).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

        var apply = typeof(DataMigrationsController).GetMethod(nameof(DataMigrationsController.Apply))!;
        apply.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
        apply.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        apply.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }

    [HumansFact]
    public async Task Screen_lists_the_reports()
    {
        var result = await Controller(_actorId).Index(Ct);

        result.Should().BeOfType<ViewResult>()
            .Which.Model.Should().BeAssignableTo<IReadOnlyList<DataMigrationReport>>()
            .Which.Should().ContainSingle(r => string.Equals(r.Migration.Key, Key, StringComparison.Ordinal) && r.Status.Pending == 2);
    }

    [HumansFact]
    public async Task Apply_without_an_actor_is_refused_before_anything_runs()
    {
        (await Controller(actorId: null).Apply(Key, Ct)).Should().BeOfType<UnauthorizedResult>();

        _migrations.ApplyCalls.Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Apply_of_an_unknown_key_is_not_found()
    {
        (await Controller(_actorId).Apply("nope.missing", Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Apply_redirects_back_to_the_screen()
    {
        var result = await Controller(_actorId).Apply(Key, Ct);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(DataMigrationsController.Index));
        _migrations.ApplyCalls.Should().Be(1);
    }

    [HumansFact]
    public void Endpoint_is_an_anonymous_get()
    {
        var endpoint = MapStatusEndpoint();

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Should().Equal("GET");
    }

    [HumansFact]
    public async Task Endpoint_publishes_keys_counts_and_done_but_never_row_data()
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        http.Response.Body = new MemoryStream();

        await MapStatusEndpoint().RequestDelegate!(http);

        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync(Ct);
        body.Should().NotContain(PendingEmail);

        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("version", "migrations");
        var migration = json.RootElement.GetProperty("migrations").EnumerateArray().Should().ContainSingle().Subject;
        migration.EnumerateObject().Select(p => p.Name).Should().Equal("key", "pending", "done");
        migration.GetProperty("key").GetString().Should().Be(Key);
        migration.GetProperty("pending").GetInt32().Should().Be(2);
        migration.GetProperty("done").GetBoolean().Should().BeFalse();
    }

    private static RouteEndpoint MapStatusEndpoint()
    {
        var app = WebApplication.CreateSlimBuilder().Build();
        new SectionEndpoints().MapEndpoints(app);
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, "/api/data-migrations", StringComparison.Ordinal));
    }

    private DataMigrationsController Controller(Guid? actorId)
    {
        var claims = actorId is { } id ? [new Claim(ClaimTypes.NameIdentifier, id.ToString())] : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            RequestServices = _services,
        };
        return new DataMigrationsController(Substitute.For<IUserService>(), Service)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>(),
        };
    }

    /// <summary>One migration over an in-memory pending list: apply copies everything and clears it.</summary>
    private sealed class FakeMigrations(List<string> pending) : ISectionDataMigrations
    {
        public int ApplyCalls { get; private set; }
        public Guid? LastActor { get; private set; }

        public IEnumerable<DataMigration> Migrations() =>
        [
            new(Key, "Fake", "Copy the fake rows", "Nothing",
                (_, _) => Task.FromResult(new DataMigrationStatus(
                    ["Email"], [.. pending.Select(p => (IReadOnlyList<string>)[p])])),
                (_, actor, _) =>
                {
                    ApplyCalls++;
                    LastActor = actor;
                    var written = pending.Count;
                    pending.Clear();
                    return Task.FromResult(written);
                }),
        ];
    }
}
