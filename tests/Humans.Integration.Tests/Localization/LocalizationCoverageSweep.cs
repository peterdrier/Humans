using System.Net;
using System.Text;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Humans.Integration.Tests.Localization;

/// <summary>
/// Localization-coverage sweep. Renders the whole app through the pseudo-localizer
/// (<see cref="PseudoLocalizationWebApplicationFactory"/>) so every localized string is
/// bracketed, then crawls every GET page and checks the rendered text:
/// <list type="bullet">
///   <item>Public pages (anonymous / <c>AppAccess</c>): unbracketed prose = hard-coded string that should be localized.</item>
///   <item>Admin/role-gated pages: bracketed text = a string that should have stayed English-only.</item>
/// </list>
/// The first run is report-only (it never fails on findings) — it writes
/// <c>localization-sweep-report.md</c> at the repo root and echoes it to test output. How
/// aggressively (if at all) it should gate is decided after we read that first report; the
/// intended cadence is a bi-weekly maintenance sweep, not a per-build blocker.
/// </summary>
public sealed class LocalizationCoverageSweep(
    PseudoLocalizationWebApplicationFactory factory,
    ITestOutputHelper output) : IClassFixture<PseudoLocalizationWebApplicationFactory>
{
    /// <summary>
    /// Gates the sweep out of normal CI runs (it boots a Postgres container and crawls every
    /// page, ~20s). Set <c>RUN_LOCALIZATION_SWEEP=1</c> to run it — e.g. from the bi-weekly
    /// maintenance job. How (and whether) it should gate on findings is decided separately.
    /// </summary>
    public static bool SweepEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LOCALIZATION_SWEEP"), "1", StringComparison.Ordinal);

    [HumansFact(
        Timeout = 600_000,
        Skip = "Set RUN_LOCALIZATION_SWEEP=1 to run the localization-coverage sweep (bi-weekly maintenance job).",
        SkipUnless = nameof(SweepEnabled))]
    public async Task Public_pages_are_localized_and_admin_pages_are_not()
    {
        var catalog = SweepRouteCatalog.Build(factory.Services);

        var publicClient = CreateClient();
        await factory.SignInAsFullyOnboardedAsync(publicClient, DevPersona.Volunteer);

        var adminClient = CreateClient();
        await factory.SignInAsFullyOnboardedAsync(adminClient, DevPersona.Admin);

        var results = new List<RouteScanResult>(catalog.Crawlable.Count);
        foreach (var route in catalog.Crawlable)
        {
            var client = route.Audience == Audience.Public ? publicClient : adminClient;
            results.Add(await ScanRoute(client, route));
        }

        var report = SweepReport.Build(results, catalog.SkippedParamRoutes);
        var path = SweepReport.Write(report);

        output.WriteLine(report);
        output.WriteLine($"{Environment.NewLine}Full report written to: {path}");

        results.Should().NotBeEmpty("the sweep must crawl at least one page");
    }

    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string? LocationOf(HttpResponseMessage response)
    {
        if (response.Headers.Location is not { } location)
            return null;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static async Task<RouteScanResult> ScanRoute(HttpClient client, RouteCandidate route)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(route.Url, TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            return RouteScanResult.Error(route, $"{ex.GetType().Name}: {ex.Message}");
        }

        var status = (int)response.StatusCode;
        if (response.StatusCode != HttpStatusCode.OK)
            return RouteScanResult.NonOk(route, status, LocationOf(response));

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            return RouteScanResult.NonHtml(route, status, contentType);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var runs = RenderedTextScanner.ExtractTextRuns(html);

        var findings = route.Audience == Audience.Public
            ? runs.Where(r => !RenderedTextScanner.HasMarker(r)).Distinct(StringComparer.Ordinal).ToList()
            : runs.Where(RenderedTextScanner.HasMarker).Distinct(StringComparer.Ordinal).ToList();

        return RouteScanResult.Scanned(route, status, findings);
    }
}

internal enum ScanOutcome
{
    Scanned,
    NonOk,
    NonHtml,
    Error,
}

internal sealed record RouteScanResult(
    RouteCandidate Route,
    ScanOutcome Outcome,
    int StatusCode,
    string? Detail,
    IReadOnlyList<string> Findings)
{
    public static RouteScanResult Scanned(RouteCandidate route, int status, IReadOnlyList<string> findings) =>
        new(route, ScanOutcome.Scanned, status, null, findings);

    public static RouteScanResult NonOk(RouteCandidate route, int status, string? location) =>
        new(route, ScanOutcome.NonOk, status, location, []);

    public static RouteScanResult NonHtml(RouteCandidate route, int status, string? contentType) =>
        new(route, ScanOutcome.NonHtml, status, contentType, []);

    public static RouteScanResult Error(RouteCandidate route, string error) =>
        new(route, ScanOutcome.Error, 0, error, []);
}

internal static class SweepReport
{
    private const int MaxRunLength = 160;

    public static string Build(IReadOnlyList<RouteScanResult> results, IReadOnlyList<string> skippedParamRoutes)
    {
        var scanned = results.Where(r => r.Outcome == ScanOutcome.Scanned).ToList();
        var publicWithBare = scanned
            .Where(r => r.Route.Audience == Audience.Public && r.Findings.Count > 0)
            .OrderBy(r => r.Route.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var adminWithLocalized = scanned
            .Where(r => r.Route.Audience == Audience.Restricted && r.Findings.Count > 0)
            .OrderBy(r => r.Route.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nonOk = results.Where(r => r.Outcome == ScanOutcome.NonOk).ToList();
        var nonHtml = results.Where(r => r.Outcome == ScanOutcome.NonHtml).ToList();
        var errors = results.Where(r => r.Outcome == ScanOutcome.Error).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Localization coverage sweep");
        sb.AppendLine();
        sb.AppendLine("Rendered through the pseudo-localizer (every localized string is bracketed `⟦…⟧`).");
        sb.AppendLine("Unbracketed prose on a public page = not localized; bracketed text on an admin page = should be English-only.");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLineInv($"- Crawlable GET pages: {results.Count}");
        sb.AppendLineInv($"- Scanned (200 text/html): {scanned.Count}");
        sb.AppendLineInv($"- Public pages with hard-coded text: {publicWithBare.Count}");
        sb.AppendLineInv($"- Admin pages with localized text: {adminWithLocalized.Count}");
        sb.AppendLineInv($"- Coverage gaps — non-200 responses: {nonOk.Count}");
        sb.AppendLineInv($"- Non-HTML responses (skipped): {nonHtml.Count}");
        sb.AppendLineInv($"- Request errors: {errors.Count}");
        sb.AppendLineInv($"- Coverage gaps — parameterized routes (need seed data): {skippedParamRoutes.Count}");
        sb.AppendLine();

        sb.AppendLine("## Public pages — hard-coded strings that should be localized");
        if (publicWithBare.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        foreach (var result in publicWithBare)
        {
            sb.AppendLine();
            sb.AppendLineInv($"### {result.Route.Url}  ({result.Route.Controller}/{result.Route.Action})");
            var likelyStatic = result.Findings.Where(f => !IsLikelyDynamic(f)).ToList();
            var likelyDynamic = result.Findings.Where(IsLikelyDynamic).ToList();
            foreach (var finding in likelyStatic)
                sb.AppendLineInv($"- {Display(finding)}");
            foreach (var finding in likelyDynamic)
                sb.AppendLineInv($"- _(likely dynamic data)_ {Display(finding)}");
        }
        sb.AppendLine();

        sb.AppendLine("## Admin pages — localized strings that should be English-only");
        if (adminWithLocalized.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        foreach (var result in adminWithLocalized)
        {
            sb.AppendLine();
            sb.AppendLineInv($"### {result.Route.Url}  ({result.Route.Controller}/{result.Route.Action})");
            foreach (var finding in result.Findings)
                sb.AppendLineInv($"- {Display(finding)}");
        }
        sb.AppendLine();

        AppendList(sb, "## Coverage gaps — non-200 responses", nonOk
            .OrderBy(r => r.Route.Url, StringComparer.OrdinalIgnoreCase)
            .Select(r => FormattableString.Invariant(
                $"{r.Route.Url} ({r.Route.Controller}/{r.Route.Action}) → {r.StatusCode}{(r.Detail is null ? "" : $" {r.Detail}")} [{r.Route.Audience}]")));

        AppendList(sb, "## Non-HTML responses (skipped)", nonHtml
            .OrderBy(r => r.Route.Url, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Route.Url} ({r.Route.Controller}/{r.Route.Action}) → {r.Detail ?? "?"}"));

        AppendList(sb, "## Request errors", errors
            .Select(r => $"{r.Route.Url} ({r.Route.Controller}/{r.Route.Action}) → {r.Detail}"));

        AppendList(sb, "## Coverage gaps — parameterized routes (need seed data)", skippedParamRoutes);

        return sb.ToString();
    }

    private static void AppendLineInv(this StringBuilder sb, FormattableString line) =>
        sb.AppendLine(FormattableString.Invariant(line));

    public static string Write(string report)
    {
        var path = Path.Combine(FindRepoRoot(), "localization-sweep-report.md");
        File.WriteAllText(path, report);
        return path;
    }

    // Cheap first-pass classifier so the public-page findings separate obvious dynamic data
    // (emails, seeded persona tokens) from real hard-coded literals; refined after the first run.
    private static bool IsLikelyDynamic(string run)
    {
        var value = run.Trim();
        return value.Contains('@', StringComparison.Ordinal)
            || value.StartsWith("dev-", StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(string run)
    {
        var trimmed = run.Trim();
        if (trimmed.Length > MaxRunLength)
            trimmed = trimmed[..MaxRunLength] + "…";
        return "`" + trimmed.Replace("`", "'", StringComparison.Ordinal) + "`";
    }

    private static void AppendList(StringBuilder sb, string heading, IEnumerable<string> items)
    {
        var list = items.ToList();
        sb.AppendLine(heading);
        if (list.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            foreach (var item in list)
                sb.AppendLineInv($"- {item}");
        }
        sb.AppendLine();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
